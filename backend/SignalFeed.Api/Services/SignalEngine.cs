using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class SignalEngine
{
    private const int ScanWindowSize = 10;
    private const decimal ScoreThreshold = 70m;
    private const int MaxReturnedSignals = 20;
    private static readonly TimeSpan ActiveSymbolWindow = TimeSpan.FromMinutes(30);

    private readonly FinnhubService _finnhubService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly NewsService _newsService;
    private readonly ILogger<SignalEngine> _logger;
    private readonly decimal _volumeSpikeThreshold;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, DateTimeOffset> _activeSymbols = new(StringComparer.Ordinal);
    private readonly List<StockSignal> _recentSignals = [];
    private int _scanOffset;

    public SignalEngine(
        FinnhubService finnhubService,
        SymbolUniverseService symbolUniverseService,
        NewsService newsService,
        IConfiguration configuration,
        ILogger<SignalEngine> logger)
    {
        _finnhubService = finnhubService;
        _symbolUniverseService = symbolUniverseService;
        _newsService = newsService;
        _logger = logger;
        _volumeSpikeThreshold = Math.Max(100_000m, configuration.GetValue<decimal?>("Scanner:VolumeSpikeThreshold") ?? 500_000m);
    }

    public async Task<ScanBatchResult> GenerateSignalsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<QuoteSnapshot>();
        var candidates = new List<StockSignal>();

        var universe = await _symbolUniverseService.GetUniverseAsync(cancellationToken);
        if (universe.Count == 0)
        {
            return new ScanBatchResult { Snapshots = snapshots, Signals = [] };
        }

        var scanSet = BuildScanSet(universe, now);
        foreach (var symbol in scanSet)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var quote = await _finnhubService.GetQuoteAsync(symbol);
            if (quote is null || quote.PreviousClose <= 0 || quote.CurrentPrice <= 0)
            {
                continue;
            }

            var changePercent = Math.Round(((quote.CurrentPrice - quote.PreviousClose) / quote.PreviousClose) * 100m, 2);
            var volumeSpike = quote.Volume >= _volumeSpikeThreshold;
            var shouldLoadNews = volumeSpike || Math.Abs(changePercent) >= 2m;
            var latestNews = shouldLoadNews
                ? await _newsService.GetLatestNewsAsync(symbol, cancellationToken)
                : null;
            var hasPositiveNews = latestNews is not null && latestNews.SentimentScore > 0.2m;
            var hasNegativeNews = latestNews is not null && latestNews.SentimentScore < -0.2m;
            var score = CalculateCompositeScore(changePercent, volumeSpike, hasPositiveNews, hasNegativeNews);
            var confidence = ResolveConfidence(score);
            var signalType = ResolveSignalType(changePercent);
            if (signalType is null)
            {
                continue;
            }

            snapshots.Add(new QuoteSnapshot
            {
                Symbol = symbol,
                CurrentPrice = Math.Round(quote.CurrentPrice, 2),
                PreviousClose = Math.Round(quote.PreviousClose, 2),
                DayHigh = Math.Round(quote.High, 2),
                DayLow = Math.Round(quote.Low, 2),
                ChangePercent = changePercent,
                ScannedAt = now
            });

            candidates.Add(new StockSignal
            {
                Symbol = symbol,
                Price = Math.Round(quote.CurrentPrice, 2),
                ChangePercent = changePercent,
                SignalType = signalType,
                Score = score,
                ActivityScore = score,
                Confidence = confidence,
                Headline = BuildHeadline(symbol, signalType, changePercent, latestNews),
                SignalReason = BuildReason(changePercent, score, volumeSpike, hasPositiveNews, hasNegativeNews),
                Source = "Scanner",
                Timestamp = now,
                ScannedAt = now
            });
        }

        var strongSignals = candidates
            .Where(signal => signal.Score >= ScoreThreshold)
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .ToList();

        var finalSignals = strongSignals
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .Take(MaxReturnedSignals)
            .ToList();

        MarkTopOpportunity(finalSignals);
        MarkTrending(finalSignals, now);
        UpdateActiveState(finalSignals, now);

        _logger.LogInformation(
            "Signal engine produced {SignalCount} signals from {ScannedCount} scanned symbols.",
            finalSignals.Count,
            scanSet.Count);

        return new ScanBatchResult
        {
            Snapshots = snapshots,
            Signals = finalSignals
        };
    }

    private List<string> BuildScanSet(IReadOnlyList<string> universe, DateTimeOffset now)
    {
        var selected = new List<string>(ScanWindowSize);
        var selectedSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in GetPrioritizedActiveSymbols(universe, now))
        {
            if (selectedSet.Add(symbol))
            {
                selected.Add(symbol);
                if (selected.Count >= ScanWindowSize)
                {
                    return selected;
                }
            }
        }

        var start = GetAndAdvanceOffset(universe.Count);
        for (var index = 0; index < universe.Count && selected.Count < ScanWindowSize; index++)
        {
            var symbol = universe[(start + index) % universe.Count];
            if (selectedSet.Add(symbol))
            {
                selected.Add(symbol);
            }
        }

        return selected;
    }

    private IReadOnlyList<string> GetPrioritizedActiveSymbols(IReadOnlyList<string> universe, DateTimeOffset now)
    {
        var universeSet = new HashSet<string>(universe, StringComparer.Ordinal);
        lock (_stateGate)
        {
            PruneOldActiveSymbols(now);
            return _activeSymbols
                .OrderByDescending(pair => pair.Value)
                .Select(pair => pair.Key)
                .Where(symbol => universeSet.Contains(symbol))
                .Take(ScanWindowSize)
                .ToList();
        }
    }

    private int GetAndAdvanceOffset(int symbolCount)
    {
        lock (_stateGate)
        {
            var current = _scanOffset;
            _scanOffset = (_scanOffset + ScanWindowSize) % Math.Max(1, symbolCount);
            return current;
        }
    }

    private void UpdateActiveState(IReadOnlyCollection<StockSignal> currentSignals, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            PruneOldActiveSymbols(now);

            foreach (var signal in currentSignals)
            {
                _activeSymbols[signal.Symbol] = now;
            }

            foreach (var signal in currentSignals)
            {
                _recentSignals.RemoveAll(existing => existing.Symbol.Equals(signal.Symbol, StringComparison.Ordinal));
                _recentSignals.Insert(0, signal);
            }

            var cutoff = now.AddMinutes(-10);
            _recentSignals.RemoveAll(signal => signal.Timestamp < cutoff);
            if (_recentSignals.Count > 100)
            {
                _recentSignals.RemoveRange(100, _recentSignals.Count - 100);
            }
        }
    }

    private void PruneOldActiveSymbols(DateTimeOffset now)
    {
        var cutoff = now - ActiveSymbolWindow;
        foreach (var stale in _activeSymbols.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
        {
            _activeSymbols.Remove(stale);
        }
    }

    private static string? ResolveSignalType(decimal changePercent)
    {
        if (changePercent > 3m)
        {
            return "SPIKE";
        }

        if (changePercent > 1.5m)
        {
            return "BULLISH";
        }

        if (changePercent < -1.5m)
        {
            return "BEARISH";
        }

        return null;
    }

    private static decimal CalculateCompositeScore(
        decimal changePercent,
        bool volumeSpike,
        bool hasPositiveNews,
        bool hasNegativeNews)
    {
        var score = Math.Abs(changePercent) * 15m;

        if (volumeSpike)
        {
            score += 40m;
        }

        if (hasPositiveNews)
        {
            score += 25m;
        }

        if (hasNegativeNews)
        {
            score += 25m;
        }

        return Math.Round(score, 2);
    }

    private static string ResolveConfidence(decimal score)
    {
        if (score > 100m)
        {
            return "HIGH";
        }

        if (score > 70m)
        {
            return "MEDIUM";
        }

        return "LOW";
    }

    private static string BuildHeadline(
        string symbol,
        string signalType,
        decimal changePercent,
        NormalizedNewsItem? latestNews)
    {
        if (latestNews is not null && !string.IsNullOrWhiteSpace(latestNews.Headline))
        {
            return latestNews.Headline;
        }

        return signalType switch
        {
            "SPIKE" => $"{symbol} surged {changePercent:+0.##;-0.##;0}% with strong upside momentum.",
            "BEARISH" => $"{symbol} dropped {changePercent:+0.##;-0.##;0}% and is under pressure.",
            "BULLISH" => $"{symbol} is trending up at {changePercent:+0.##;-0.##;0}% this cycle.",
            _ => $"{symbol} is active with a {changePercent:+0.##;-0.##;0}% move."
        };
    }

    private static string BuildReason(
        decimal changePercent,
        decimal score,
        bool volumeSpike,
        bool hasPositiveNews,
        bool hasNegativeNews)
    {
        var tags = new List<string>
        {
            $"Move {changePercent:+0.##;-0.##;0}%",
            $"Score {score:0.##}"
        };

        if (volumeSpike)
        {
            tags.Add("Volume Spike");
        }

        if (hasPositiveNews)
        {
            tags.Add("Positive News");
        }

        if (hasNegativeNews)
        {
            tags.Add("Negative News");
        }

        return string.Join(" | ", tags);
    }

    private static void MarkTopOpportunity(IReadOnlyList<StockSignal> signals)
    {
        foreach (var signal in signals)
        {
            signal.IsTopOpportunity = false;
        }

        var top = signals
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .FirstOrDefault();
        if (top is not null)
        {
            top.IsTopOpportunity = true;
        }
    }

    private void MarkTrending(IReadOnlyList<StockSignal> signals, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            var cutoff = now.AddMinutes(-5);
            _recentSignals.RemoveAll(signal => signal.Timestamp < cutoff);
            foreach (var signal in signals)
            {
                var repeats = _recentSignals.Count(previous => previous.Symbol.Equals(signal.Symbol, StringComparison.Ordinal));
                signal.IsTrending = repeats >= 2;
            }
        }
    }
}
