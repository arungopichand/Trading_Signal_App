using System.Collections.Concurrent;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class SignalEngine
{
    private const int DefaultScanWindowSize = 12;
    private const int MaxReturnedSignals = 20;
    private const decimal StrongMoveThreshold = 2m;
    private static readonly TimeSpan SignalDedupWindow = TimeSpan.FromSeconds(5);
    private const int TrendingMemoryLimit = 50;
    private const int TrendingOccurrenceThreshold = 3;
    private const decimal TrendingScoreBoost = 20m;
    private static readonly TimeSpan TrendingWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveSymbolWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();

    private readonly MarketDataService _marketDataService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly ILogger<SignalEngine> _logger;
    private readonly decimal _volumeSpikeThreshold;
    private readonly decimal _dedupScoreThreshold;
    private readonly int _scanWindowSize;
    private readonly int _maxParallelScans;
    private readonly object _stateGate = new();
    private readonly List<SignalOccurrence> _recentSignals = [];
    private readonly Dictionary<string, (DateTimeOffset Timestamp, decimal Score)> _lastAcceptedSignalBySymbol = new(StringComparer.Ordinal);
    private int _scanOffset;

    public SignalEngine(
        MarketDataService marketDataService,
        FinnhubService finnhubService,
        SymbolUniverseService symbolUniverseService,
        NewsAggregationService newsAggregationService,
        IConfiguration configuration,
        ILogger<SignalEngine> logger)
    {
        _marketDataService = marketDataService;
        _symbolUniverseService = symbolUniverseService;
        _logger = logger;
        _volumeSpikeThreshold = Math.Max(100_000m, configuration.GetValue<decimal?>("Scanner:VolumeSpikeThreshold") ?? 500_000m);
        _dedupScoreThreshold = Math.Max(1m, configuration.GetValue<decimal?>("Scanner:DedupScoreThreshold") ?? 10m);
        _scanWindowSize = Math.Clamp(configuration.GetValue<int?>("Scanner:CycleSymbolCount") ?? DefaultScanWindowSize, 10, 15);
        _maxParallelScans = Math.Clamp(configuration.GetValue<int?>("Scanner:MaxParallelSymbols") ?? 6, 5, 10);
    }

    public async Task<ScanBatchResult> GenerateSignalsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (ResolveMarketSession(now) == "CLOSED")
        {
            _logger.LogInformation("Signal scan skipped for CLOSED session at {Timestamp}.", now);
            return new ScanBatchResult
            {
                Snapshots = [],
                Signals = []
            };
        }

        var snapshots = new ConcurrentBag<QuoteSnapshot>();
        var candidates = new ConcurrentBag<StockSignal>();

        var universe = await _symbolUniverseService.GetUniverseAsync(cancellationToken);
        if (universe.Count == 0)
        {
            return new ScanBatchResult
            {
                Snapshots = [],
                Signals = []
            };
        }

        var scanSet = BuildScanSet(universe, now);
        using var semaphore = new SemaphoreSlim(_maxParallelScans, _maxParallelScans);
        var symbolTasks = scanSet.Select(async symbol =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var unified = await _marketDataService.GetUnifiedMarketDataAsync(symbol, includeNews: true, cancellationToken);
                if (unified.Price <= 0)
                {
                    return;
                }

                snapshots.Add(new QuoteSnapshot
                {
                    Symbol = unified.Symbol,
                    CurrentPrice = Math.Round(unified.Price, 2),
                    PreviousClose = Math.Round(unified.Quote.PreviousClose, 2),
                    DayHigh = Math.Round(unified.Quote.High, 2),
                    DayLow = Math.Round(unified.Quote.Low, 2),
                    ChangePercent = unified.ChangePercent,
                    ScannedAt = now
                });

                var candidate = BuildConfluenceSignal(unified, now);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // no-op, cooperative cancellation
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Symbol scan failed for {Symbol}. Continuing with remaining symbols.", symbol);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(symbolTasks);

        var finalSignals = candidates
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .Take(MaxReturnedSignals)
            .ToList();

        var snapshotList = snapshots
            .OrderByDescending(snapshot => snapshot.ChangePercent)
            .ToList();

        if (finalSignals.Count == 0 && snapshotList.Count > 0)
        {
            var snapshot = snapshotList[0];
            finalSignals.Add(new StockSignal
            {
                Symbol = snapshot.Symbol,
                CountryCode = "US",
                Price = snapshot.CurrentPrice,
                PriceRange = PriceRangeResolver.GetPriceRange(snapshot.CurrentPrice),
                ChangePercent = snapshot.ChangePercent,
                SignalType = "TRENDING",
                Score = 10m,
                ActivityScore = 10m,
                Confidence = "LOW",
                TradeReadiness = "WATCH",
                Headline = $"{snapshot.Symbol} baseline market activity.",
                SignalReason = "Fallback baseline signal to keep feed active.",
                Reasons = ["Fallback baseline"],
                Sentiment = snapshot.ChangePercent >= 0 ? "BULLISH" : "BEARISH",
                Volume = 0m,
                Flags = [],
                RepeatCount = 1,
                IsTrending = true,
                Source = "FALLBACK",
                Timestamp = now,
                ScannedAt = now
            });
        }

        ApplyTrendingBoost(finalSignals, now);
        var dedupedSignals = ApplyDeduplication(finalSignals, now);
        MarkTopOpportunity(dedupedSignals);

        _logger.LogInformation(
            "Confluence engine produced {SignalCount} signals from {ScannedCount} symbols.",
            dedupedSignals.Count,
            scanSet.Count);

        return new ScanBatchResult
        {
            Snapshots = snapshotList,
            Signals = dedupedSignals
        };
    }

    public Task<IReadOnlyList<StockSignal>> GenerateSignalsFromInputsAsync(
        IReadOnlyList<SimulatedMarketInput> inputs,
        decimal? scoreThresholdOverride = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var signals = new List<StockSignal>(inputs.Count);

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(input.Symbol) || input.CurrentPrice <= 0)
            {
                continue;
            }

            var normalizedSymbol = input.Symbol.Trim().ToUpperInvariant();
            var normalizedSentiment = NormalizeSentimentLabel(input.Sentiment);
            var news = string.IsNullOrWhiteSpace(input.Headline)
                ? null
                : new NormalizedNewsItem
                {
                    Symbol = normalizedSymbol,
                    Headline = input.Headline,
                    Summary = input.Headline,
                    Source = string.IsNullOrWhiteSpace(input.Source) ? "SIM" : input.Source,
                    Url = input.Url,
                    Datetime = input.Timestamp == default ? now : input.Timestamp,
                    Sentiment = normalizedSentiment,
                    Category = string.IsNullOrWhiteSpace(input.Category) ? "market commentary" : input.Category,
                    SentimentScore = ResolveSentimentScore(normalizedSentiment)
                };

            var previousClose = input.PreviousClose > 0 ? input.PreviousClose : input.CurrentPrice;
            var changePercent = previousClose > 0
                ? Math.Round(((input.CurrentPrice - previousClose) / previousClose) * 100m, 2)
                : 0m;

            var unified = new UnifiedMarketData
            {
                Symbol = normalizedSymbol,
                Price = input.CurrentPrice,
                ChangePercent = changePercent,
                Volume = Math.Max(0, input.Volume),
                News = news,
                Sentiment = normalizedSentiment,
                PriceSource = "SIM",
                VolumeSource = "SIM",
                Quote = new QuoteResponse
                {
                    CurrentPrice = input.CurrentPrice,
                    PreviousClose = previousClose,
                    OpenPrice = input.OpenPrice > 0 ? input.OpenPrice : previousClose,
                    High = input.High > 0 ? input.High : input.CurrentPrice,
                    Low = input.Low > 0 ? input.Low : input.CurrentPrice,
                    Volume = Math.Max(0, input.Volume),
                    Timestamp = (input.Timestamp == default ? now : input.Timestamp).ToUnixTimeSeconds()
                }
            };

            var signal = BuildConfluenceSignal(unified, now);
            if (signal is not null)
            {
                signals.Add(signal);
            }
        }

        var output = signals
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .Take(MaxReturnedSignals)
            .ToList();

        ApplyTrendingBoost(output, now);
        MarkTopOpportunity(output);
        return Task.FromResult<IReadOnlyList<StockSignal>>(output);
    }

    private void ApplyTrendingBoost(IReadOnlyList<StockSignal> signals, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            PruneRecentSignals(now);

            foreach (var signal in signals)
            {
                var symbol = signal.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var recentForSymbol = _recentSignals
                    .Where(item =>
                        item.Symbol.Equals(symbol, StringComparison.Ordinal) &&
                        item.Timestamp >= now - TrendingWindow)
                    .ToList();
                var occurrenceCount = recentForSymbol.Count + 1;
                var averageScore = occurrenceCount > 0
                    ? (recentForSymbol.Sum(item => item.Score) + signal.Score) / occurrenceCount
                    : signal.Score;

                _recentSignals.Add(new SignalOccurrence(symbol, now, signal.Score));
                TrimRecentSignals();

                signal.RepeatCount = occurrenceCount;
                if (occurrenceCount >= TrendingOccurrenceThreshold && averageScore > 70m)
                {
                    signal.IsTrending = true;
                    signal.Score = Math.Round(signal.Score + TrendingScoreBoost, 2);
                    signal.ActivityScore = Math.Round(signal.ActivityScore + TrendingScoreBoost, 2);

                    const string trendingReason = "Trending (multiple signals in short time)";
                    signal.Reasons ??= [];
                    if (!signal.Reasons.Contains(trendingReason, StringComparer.Ordinal))
                    {
                        signal.Reasons.Add(trendingReason);
                    }

                    signal.SignalReason = string.IsNullOrWhiteSpace(signal.SignalReason)
                        ? trendingReason
                        : signal.SignalReason.Contains(trendingReason, StringComparison.Ordinal)
                            ? signal.SignalReason
                            : $"{signal.SignalReason} + {trendingReason}";
                }
            }
        }
    }

    private void PruneRecentSignals(DateTimeOffset now)
    {
        var cutoff = now - TrendingWindow;
        _recentSignals.RemoveAll(item => item.Timestamp < cutoff);
        TrimRecentSignals();
    }

    private void TrimRecentSignals()
    {
        if (_recentSignals.Count <= TrendingMemoryLimit)
        {
            return;
        }

        _recentSignals.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
        _recentSignals.RemoveRange(TrendingMemoryLimit, _recentSignals.Count - TrendingMemoryLimit);
    }

    private StockSignal? BuildConfluenceSignal(UnifiedMarketData marketData, DateTimeOffset now)
    {
        var strongMove = Math.Abs(marketData.ChangePercent) > StrongMoveThreshold;
        var volumeSpike = marketData.Volume > _volumeSpikeThreshold;
        var hasNews = marketData.News is not null;
        var bullishNews = string.Equals(marketData.Sentiment, "BULLISH", StringComparison.OrdinalIgnoreCase);
        var bearishNews = string.Equals(marketData.Sentiment, "BEARISH", StringComparison.OrdinalIgnoreCase);

        var factorCount = CountTrue(strongMove, volumeSpike, hasNews, bullishNews, bearishNews);
        if (factorCount < 2)
        {
            return null;
        }

        var score = 0m;
        if (strongMove)
        {
            score += 40m;
        }

        if (volumeSpike)
        {
            score += 30m;
        }

        if (bullishNews || bearishNews)
        {
            score += 30m;
        }

        var confidence = factorCount >= 3
            ? "HIGH"
            : factorCount == 2
                ? "MEDIUM"
                : "LOW";

        var signalType = strongMove && volumeSpike
            ? "SPIKE"
            : bullishNews
                ? "BULLISH"
                : bearishNews
                    ? "BEARISH"
                    : "TRENDING";

        var reasons = new List<string>();
        if (strongMove)
        {
            reasons.Add("Strong price move");
        }

        if (volumeSpike)
        {
            reasons.Add("Volume spike");
        }

        if (bullishNews)
        {
            reasons.Add("Positive news");
        }

        if (bearishNews)
        {
            reasons.Add("Negative news");
        }

        var reasonText = string.Join(" + ", reasons);
        var headline = marketData.News?.Headline;
        if (string.IsNullOrWhiteSpace(headline))
        {
            headline = $"{marketData.Symbol} {signalType.ToLowerInvariant()} setup ({marketData.ChangePercent:+0.##;-0.##;0}%).";
        }

        var sentiment = bullishNews
            ? "BULLISH"
            : bearishNews
                ? "BEARISH"
                : "NEUTRAL";

        return new StockSignal
        {
            Symbol = marketData.Symbol,
            CountryCode = "US",
            Price = Math.Round(marketData.Price, 2),
            PriceRange = PriceRangeResolver.GetPriceRange(marketData.Price),
            ChangePercent = Math.Round(marketData.ChangePercent, 2),
            SignalType = signalType,
            Score = score,
            ActivityScore = score,
            Confidence = confidence,
            TradeReadiness = factorCount >= 3 ? "READY" : "WATCH",
            Headline = headline,
            Url = marketData.News?.Url ?? string.Empty,
            SignalReason = reasonText,
            Reasons = reasons,
            Volume = Math.Round(marketData.Volume, 0),
            Sentiment = sentiment,
            NewsCategory = marketData.News?.Category ?? string.Empty,
            Momentum = Math.Round(marketData.ChangePercent, 2),
            VolumeRatio = marketData.Volume > 0 ? Math.Round(marketData.Volume / _volumeSpikeThreshold, 2) : 0m,
            RelativeVolume = marketData.Volume > 0 ? Math.Round(marketData.Volume / _volumeSpikeThreshold, 2) : 0m,
            Source = $"{marketData.PriceSource}/{marketData.VolumeSource}",
            Flags = [],
            RepeatCount = 1,
            IsTrending = signalType == "TRENDING",
            Timestamp = now,
            ScannedAt = now
        };
    }

    private static int CountTrue(params bool[] values)
    {
        return values.Count(value => value);
    }

    private List<string> BuildScanSet(IReadOnlyList<string> universe, DateTimeOffset now)
    {
        if (universe.Count <= _scanWindowSize)
        {
            return universe.ToList();
        }

        var universeSet = new HashSet<string>(universe, StringComparer.Ordinal);
        var selected = new List<string>(_scanWindowSize);
        var selectedSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in GetPrioritizedSymbols(now))
        {
            if (!universeSet.Contains(symbol))
            {
                continue;
            }

            if (selectedSet.Add(symbol))
            {
                selected.Add(symbol);
                if (selected.Count >= _scanWindowSize)
                {
                    return selected;
                }
            }
        }

        int start;
        lock (_stateGate)
        {
            start = _scanOffset;
            _scanOffset = (_scanOffset + _scanWindowSize) % universe.Count;
        }

        for (var i = 0; i < universe.Count && selected.Count < _scanWindowSize; i++)
        {
            var symbol = universe[(start + i) % universe.Count];
            if (selectedSet.Add(symbol))
            {
                selected.Add(symbol);
            }
        }

        return selected;
    }

    private IReadOnlyList<StockSignal> ApplyDeduplication(IReadOnlyList<StockSignal> signals, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            var kept = new List<StockSignal>(signals.Count);

            foreach (var signal in signals)
            {
                var symbol = signal.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                if (_lastAcceptedSignalBySymbol.TryGetValue(symbol, out var previous))
                {
                    var withinWindow = now - previous.Timestamp <= SignalDedupWindow;
                    var scoreChange = Math.Abs(signal.Score - previous.Score);
                    if (withinWindow && scoreChange < _dedupScoreThreshold)
                    {
                        _logger.LogDebug(
                            "Dedup skipped signal for {Symbol}. Age={AgeMs}ms ScoreDelta={ScoreDelta:0.##}.",
                            symbol,
                            (now - previous.Timestamp).TotalMilliseconds,
                            scoreChange);
                        continue;
                    }
                }

                _lastAcceptedSignalBySymbol[symbol] = (now, signal.Score);
                kept.Add(signal);
            }

            var staleCutoff = now - SignalDedupWindow - TimeSpan.FromSeconds(5);
            var staleSymbols = _lastAcceptedSignalBySymbol
                .Where(pair => pair.Value.Timestamp < staleCutoff)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var symbol in staleSymbols)
            {
                _lastAcceptedSignalBySymbol.Remove(symbol);
            }

            return kept;
        }
    }

    private IReadOnlyList<string> GetPrioritizedSymbols(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            PruneRecentSignals(now);

            // Priority #1: recently triggered symbols.
            var recentlyTriggered = _recentSignals
                .Where(item => item.Timestamp >= now - ActiveSymbolWindow)
                .GroupBy(item => item.Symbol, StringComparer.Ordinal)
                .OrderByDescending(group => group.Max(item => item.Timestamp))
                .ThenByDescending(group => group.Count())
                .Select(group => group.Key)
                .ToList();

            // Priority #2: trending symbols.
            var trending = _recentSignals
                .Where(item => item.Timestamp >= now - TrendingWindow)
                .GroupBy(item => item.Symbol, StringComparer.Ordinal)
                .Where(group => group.Count() >= TrendingOccurrenceThreshold)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Average(item => item.Score))
                .ThenByDescending(group => group.Max(item => item.Timestamp))
                .Select(group => group.Key)
                .ToList();

            return recentlyTriggered
                .Concat(trending)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    private void MarkTopOpportunity(IReadOnlyList<StockSignal> signals)
    {
        foreach (var signal in signals)
        {
            signal.IsTopOpportunity = false;
        }

        var top = signals.FirstOrDefault();
        if (top is not null)
        {
            top.IsTopOpportunity = true;
            _marketDataService.MarkTopOpportunity(top.Symbol);
        }
    }

    private static string NormalizeSentimentLabel(string sentiment)
    {
        var normalized = sentiment?.Trim().ToUpperInvariant();
        return normalized switch
        {
            "BULLISH" => "BULLISH",
            "BEARISH" => "BEARISH",
            _ => "NEUTRAL"
        };
    }

    private static decimal ResolveSentimentScore(string sentiment)
    {
        return sentiment switch
        {
            "BULLISH" => 0.6m,
            "BEARISH" => -0.6m,
            _ => 0m
        };
    }

    private static string ResolveMarketSession(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, EasternTimeZone);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return "CLOSED";
        }

        var time = local.TimeOfDay;
        if (time >= TimeSpan.FromHours(4) && time < TimeSpan.FromHours(9.5))
        {
            return "PREMARKET";
        }

        if (time >= TimeSpan.FromHours(9.5) && time < TimeSpan.FromHours(16))
        {
            return "REGULAR";
        }

        if (time >= TimeSpan.FromHours(16) && time < TimeSpan.FromHours(20))
        {
            return "AFTER_HOURS";
        }

        return "CLOSED";
    }

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }

    private sealed record SignalOccurrence(string Symbol, DateTimeOffset Timestamp, decimal Score);
}
