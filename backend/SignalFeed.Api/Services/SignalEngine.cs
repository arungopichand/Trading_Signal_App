using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class SignalEngine
{
    private const int ScanWindowSize = 10;
    private const decimal ScoreThreshold = 70m;
    private const decimal MinimumSignalScore = 60m;
    private const int MaxReturnedSignals = 20;
    private const decimal SpikeThreshold = 3m;
    private const decimal BearishThreshold = -3m;
    private const decimal BullishThreshold = 1.2m;
    private const decimal AccelerationThreshold = 1.2m;
    private const decimal MomentumAccelerationThresholdPerSecond = 0.03m;
    private const int RepeatedUpTickThreshold = 3;
    private const decimal RelativeVolumeSpikeThreshold = 1.8m;
    private const int TrendingSignalMemory = 50;
    private const int TrendingOccurrenceThreshold = 3;
    private static readonly TimeSpan TrendingSymbolWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveSymbolWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MomentumWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MarketContextCacheWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FundamentalsCacheWindow = TimeSpan.FromHours(6);
    private static readonly string[] HighPriorityNewsKeywords = ["earnings", "acquisition", "fda", "contract", "guidance"];
    private static readonly string[] LowPriorityNewsKeywords = ["market commentary", "commentary", "generic"];
    private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();

    private readonly FinnhubService _finnhubService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly NewsService _newsService;
    private readonly ILogger<SignalEngine> _logger;
    private readonly decimal _volumeSpikeThreshold;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, DateTimeOffset> _activeSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _lastChangePercentBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PriceSample> _lastPriceSampleBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _upTickStreakBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _momentumTriggeredBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _momentumDetectedAtBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<decimal>> _recentVolumeBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FundamentalsSnapshot> _fundamentalsCache = new(StringComparer.Ordinal);
    private readonly List<SignalOccurrence> _recentSignalOccurrences = [];
    private readonly List<StockSignal> _recentSignals = [];
    private int _scanOffset;
    private (DateTimeOffset CachedAt, string IndexDirection) _marketContextCache = (DateTimeOffset.MinValue, "FLAT");

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
        var marketSession = ResolveMarketSession(now);
        var sessionTuning = ResolveSessionTuning(marketSession);
        var marketIsClosed = marketSession == "CLOSED";
        if (marketIsClosed)
        {
            _logger.LogInformation(
                "Signal scan skipped for CLOSED session at {Timestamp}. News-only mode remains active via news ingestion.",
                now);
            return new ScanBatchResult
            {
                Snapshots = [],
                Signals = []
            };
        }

        var indexDirection = await ResolveIndexDirectionAsync(now);
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
            var gapPercent = quote.OpenPrice > 0
                ? Math.Round(((quote.OpenPrice - quote.PreviousClose) / quote.PreviousClose) * 100m, 2)
                : 0m;
            var previousChange = GetPreviousChange(symbol);
            var momentumMetrics = UpdateMomentumMetrics(symbol, quote.CurrentPrice, now);
            var accelerationValue = Math.Round(momentumMetrics.AccelerationPerSecond, 4);
            var isAccelerating = (Math.Abs(changePercent - previousChange) >= AccelerationThreshold &&
                                 Math.Sign(changePercent) == Math.Sign(previousChange == 0 ? changePercent : previousChange)) ||
                                 Math.Abs(accelerationValue) >= MomentumAccelerationThresholdPerSecond;
            var repeatedUpTicks = momentumMetrics.RepeatedUpTicks;
            var strongMomentum = momentumMetrics.StrongMomentum;
            var relativeVolume = UpdateAndGetRelativeVolume(symbol, quote.Volume);
            var momentumDetectedAt = TrackMomentumDetection(symbol, changePercent, strongMomentum, now);
            var volumeThreshold = sessionTuning.ReducedVolumeRequirement
                ? _volumeSpikeThreshold * 0.6m
                : _volumeSpikeThreshold;
            var relativeVolumeThreshold = sessionTuning.ReducedVolumeRequirement
                ? 1.35m
                : RelativeVolumeSpikeThreshold;
            var volumeSpike = quote.Volume >= volumeThreshold || relativeVolume >= relativeVolumeThreshold;
            var isTrendingMomentum = IsRepeatedMomentum(symbol, now);
            var shouldLoadNews = volumeSpike || Math.Abs(changePercent) >= 1.5m || isAccelerating || repeatedUpTicks || strongMomentum || sessionTuning.PrioritizeNews;
            var latestNews = shouldLoadNews
                ? await _newsService.GetLatestNewsAsync(symbol, cancellationToken)
                : null;

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

            var candidate = BuildSignalCandidate(
                symbol,
                quote,
                latestNews,
                now,
                marketSession,
                indexDirection,
                sessionTuning,
                changePercent,
                gapPercent,
                accelerationValue,
                isAccelerating,
                repeatedUpTicks,
                strongMomentum,
                isTrendingMomentum,
                volumeSpike,
                relativeVolume,
                momentumDetectedAt,
                marketIsClosed);
            if (candidate is null)
            {
                SetPreviousChange(symbol, changePercent);
                continue;
            }

            candidates.Add(candidate);
            SetPreviousChange(symbol, changePercent);
        }

        var finalSignals = FinalizeSignals(candidates, sessionTuning.ScoreThreshold, now);
        await EnrichSignalsWithAdvancedFactorsAsync(finalSignals, cancellationToken);

        _logger.LogInformation(
            "Signal engine produced {SignalCount} signals from {ScannedCount} scanned symbols. Session={Session}, IndexDirection={IndexDirection}.",
            finalSignals.Count,
            scanSet.Count,
            marketSession,
            indexDirection);

        return new ScanBatchResult
        {
            Snapshots = snapshots,
            Signals = finalSignals
        };
    }

    public async Task<IReadOnlyList<StockSignal>> GenerateSignalsFromInputsAsync(
        IReadOnlyList<SimulatedMarketInput> inputs,
        decimal? scoreThresholdOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var marketSession = "REGULAR";
        var sessionTuning = ResolveSessionTuning(marketSession);
        const string indexDirection = "FLAT";
        var candidates = new List<StockSignal>(inputs.Count);

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(input.Symbol) || input.PreviousClose <= 0 || input.CurrentPrice <= 0)
            {
                continue;
            }

            var symbol = input.Symbol.Trim().ToUpperInvariant();
            var quote = new QuoteResponse
            {
                CurrentPrice = input.CurrentPrice,
                PreviousClose = input.PreviousClose,
                OpenPrice = input.OpenPrice <= 0 ? input.PreviousClose : input.OpenPrice,
                High = input.High <= 0 ? Math.Max(input.CurrentPrice, input.PreviousClose) : input.High,
                Low = input.Low <= 0 ? Math.Min(input.CurrentPrice, input.PreviousClose) : input.Low,
                Volume = Math.Max(0, input.Volume),
                Timestamp = input.Timestamp.ToUnixTimeSeconds()
            };

            var latestNews = string.IsNullOrWhiteSpace(input.Headline)
                ? null
                : new NormalizedNewsItem
                {
                    Symbol = symbol,
                    Headline = input.Headline,
                    Summary = input.Headline,
                    Source = string.IsNullOrWhiteSpace(input.Source) ? "SIM" : input.Source,
                    Url = input.Url,
                    Datetime = input.Timestamp,
                    Sentiment = NormalizeSentimentLabel(input.Sentiment),
                    Category = string.IsNullOrWhiteSpace(input.Category) ? "market commentary" : input.Category,
                    SentimentScore = ResolveSentimentScore(input.Sentiment)
                };

            var changePercent = Math.Round(((quote.CurrentPrice - quote.PreviousClose) / quote.PreviousClose) * 100m, 2);
            var gapPercent = quote.OpenPrice > 0
                ? Math.Round(((quote.OpenPrice - quote.PreviousClose) / quote.PreviousClose) * 100m, 2)
                : 0m;
            var previousChange = GetPreviousChange(symbol);
            var observedAt = input.Timestamp == default ? now : input.Timestamp;
            var momentumMetrics = UpdateMomentumMetrics(symbol, quote.CurrentPrice, observedAt);
            var accelerationValue = Math.Round(momentumMetrics.AccelerationPerSecond, 4);
            var isAccelerating = (Math.Abs(changePercent - previousChange) >= AccelerationThreshold &&
                                 Math.Sign(changePercent) == Math.Sign(previousChange == 0 ? changePercent : previousChange)) ||
                                 Math.Abs(accelerationValue) >= MomentumAccelerationThresholdPerSecond;
            var repeatedUpTicks = momentumMetrics.RepeatedUpTicks;
            var strongMomentum = momentumMetrics.StrongMomentum;
            var relativeVolume = UpdateAndGetRelativeVolume(symbol, quote.Volume);
            var momentumDetectedAt = TrackMomentumDetection(symbol, changePercent, strongMomentum, now);
            var volumeThreshold = _volumeSpikeThreshold;
            var relativeVolumeThreshold = RelativeVolumeSpikeThreshold;
            var volumeSpike = quote.Volume >= volumeThreshold || relativeVolume >= relativeVolumeThreshold;
            var isTrendingMomentum = IsRepeatedMomentum(symbol, now);

            var candidate = BuildSignalCandidate(
                symbol,
                quote,
                latestNews,
                now,
                marketSession,
                indexDirection,
                sessionTuning,
                changePercent,
                gapPercent,
                accelerationValue,
                isAccelerating,
                repeatedUpTicks,
                strongMomentum,
                isTrendingMomentum,
                volumeSpike,
                relativeVolume,
                momentumDetectedAt,
                marketClosed: false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }

            SetPreviousChange(symbol, changePercent);
        }

        var threshold = scoreThresholdOverride ?? sessionTuning.ScoreThreshold;
        return FinalizeSignals(candidates, threshold, now);
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

    private List<StockSignal> FinalizeSignals(
        IReadOnlyList<StockSignal> candidates,
        decimal scoreThreshold,
        DateTimeOffset now)
    {
        var strongSignals = candidates
            .Where(signal => signal.Score >= scoreThreshold)
            .OrderByDescending(signal => signal.Score)
            .ThenByDescending(signal => signal.Timestamp)
            .ToList();

        var finalSignals = strongSignals
            .Take(MaxReturnedSignals)
            .ToList();

        MarkTopOpportunity(finalSignals);
        MarkTrending(finalSignals, now);
        UpdateActiveState(finalSignals, now);
        return finalSignals;
    }

    private StockSignal? BuildSignalCandidate(
        string symbol,
        QuoteResponse quote,
        NormalizedNewsItem? latestNews,
        DateTimeOffset now,
        string marketSession,
        string indexDirection,
        SessionTuning sessionTuning,
        decimal changePercent,
        decimal gapPercent,
        decimal accelerationValue,
        bool isAccelerating,
        bool repeatedUpTicks,
        bool strongMomentum,
        bool isTrendingMomentum,
        bool volumeSpike,
        decimal relativeVolume,
        DateTimeOffset? momentumDetectedAt,
        bool marketClosed)
    {
        var bullishNews = string.Equals(latestNews?.Sentiment, "BULLISH", StringComparison.Ordinal);
        var bearishNews = string.Equals(latestNews?.Sentiment, "BEARISH", StringComparison.Ordinal);
        var hasNews = latestNews is not null;
        var highPriorityNews = IsHighPriorityNews(latestNews);
        var lowPriorityNews = IsLowPriorityNews(latestNews);
        var positiveNewsWithRisingPrice = bullishNews && changePercent > 0;
        if (Math.Abs(changePercent) < 0.5m)
        {
            return null;
        }

        var hasMomentumFactor = strongMomentum || repeatedUpTicks || isAccelerating || isTrendingMomentum;
        var coreFactorCount = CountCoreFactors(
            hasPriceMove: Math.Abs(changePercent) >= 0.5m,
            volumeSpike: volumeSpike,
            hasNews: hasNews,
            hasMomentum: hasMomentumFactor);
        if (coreFactorCount < 2)
        {
            return null;
        }

        var score = CalculateCompositeScore(
            changePercent,
            volumeSpike,
            bullishNews,
            bearishNews,
            hasNews,
            isTrendingMomentum,
            isAccelerating,
            strongMomentum,
            highPriorityNews,
            lowPriorityNews,
            positiveNewsWithRisingPrice,
            gapPercent,
            marketClosed,
            sessionTuning.PrioritizeNews);
        if (score < MinimumSignalScore)
        {
            return null;
        }

        var confidence = ResolveConfidence(score);
        var signalType = ResolveSignalType(
            changePercent,
            bullishNews,
            bearishNews,
            hasNews,
            isTrendingMomentum,
            volumeSpike);
        if (signalType is null)
        {
            return null;
        }

        if (sessionTuning.RequireVolumeSpike && !volumeSpike)
        {
            return null;
        }

        var confirmations = CountConfirmations(
            volumeSpike,
            bullishNews || bearishNews,
            isAccelerating,
            isTrendingMomentum,
            Math.Abs(gapPercent) >= 1.5m,
            Math.Abs(changePercent) >= 1.5m);
        if (!PassNoiseGate(signalType, score, confirmations, hasNews, Math.Abs(changePercent), volumeSpike, sessionTuning))
        {
            return null;
        }

        var sentimentAligned = IsSentimentAligned(signalType, bullishNews, bearishNews);
        var tradeReadiness = ResolveTradeReadiness(score, volumeSpike, sentimentAligned);
        var sentiment = bullishNews ? "BULLISH" : bearishNews ? "BEARISH" : "NEUTRAL";

        return new StockSignal
        {
            Symbol = symbol,
            CountryCode = "US",
            Price = Math.Round(quote.CurrentPrice, 2),
            PriceRange = PriceRangeResolver.GetPriceRange(quote.CurrentPrice),
            ChangePercent = changePercent,
            SignalType = signalType,
            Score = score,
            ActivityScore = score,
            Confidence = confidence,
            TradeReadiness = tradeReadiness,
            VolumeRatio = Math.Round(relativeVolume, 2),
            Momentum = Math.Round(changePercent, 2),
            Sentiment = sentiment,
            Acceleration = accelerationValue,
            GapPercent = gapPercent,
            NewsCategory = latestNews?.Category ?? string.Empty,
            RepeatCount = 0,
            MomentumDetectedAt = momentumDetectedAt,
            Headline = BuildHeadline(symbol, signalType, changePercent, latestNews),
            Url = latestNews?.Url ?? string.Empty,
            SignalReason = BuildReason(
                changePercent,
                gapPercent,
                score,
                volumeSpike,
                relativeVolume,
                isAccelerating,
                strongMomentum,
                isTrendingMomentum,
                bullishNews,
                bearishNews,
                highPriorityNews,
                marketSession,
                indexDirection,
                latestNews),
            Source = latestNews?.Source ?? "Scanner",
            Timestamp = latestNews?.Datetime ?? now,
            ScannedAt = now,
            FloatShares = null,
            InstitutionalOwnership = null,
            MarketCap = null,
            Volume = Math.Round(quote.Volume, 0),
            Flags = BuildAdvancedFlags(
                changePercent,
                volumeSpike,
                relativeVolume,
                bullishNews,
                bearishNews,
                isTrendingMomentum,
                null),
            RelativeVolume = Math.Round(relativeVolume, 2),
        };
    }

    private DateTimeOffset? TrackMomentumDetection(string symbol, decimal changePercent, bool strongMomentum, DateTimeOffset now)
    {
        var crossed = changePercent > SpikeThreshold || changePercent < BearishThreshold || strongMomentum;
        lock (_stateGate)
        {
            var previouslyTriggered = _momentumTriggeredBySymbol.GetValueOrDefault(symbol, false);
            if (crossed && !previouslyTriggered)
            {
                _momentumTriggeredBySymbol[symbol] = true;
                _momentumDetectedAtBySymbol[symbol] = now;
                return now;
            }

            if (!crossed)
            {
                _momentumTriggeredBySymbol[symbol] = false;
            }

            return _momentumDetectedAtBySymbol.GetValueOrDefault(symbol);
        }
    }

    private MomentumMetrics UpdateMomentumMetrics(string symbol, decimal currentPrice, DateTimeOffset observedAt)
    {
        lock (_stateGate)
        {
            var accelerationPerSecond = 0m;
            var repeatedUpTicks = false;

            if (_lastPriceSampleBySymbol.TryGetValue(symbol, out var previousSample))
            {
                var elapsedSeconds = Math.Max((decimal)(observedAt - previousSample.Timestamp).TotalSeconds, 0.05m);
                var percentDelta = previousSample.Price <= 0
                    ? 0m
                    : ((currentPrice - previousSample.Price) / previousSample.Price) * 100m;
                accelerationPerSecond = Math.Round(percentDelta / elapsedSeconds, 4);

                var upStreak = _upTickStreakBySymbol.GetValueOrDefault(symbol, 0);
                if (currentPrice > previousSample.Price)
                {
                    upStreak++;
                }
                else if (currentPrice < previousSample.Price)
                {
                    upStreak = 0;
                }

                _upTickStreakBySymbol[symbol] = upStreak;
                repeatedUpTicks = upStreak >= RepeatedUpTickThreshold;
            }
            else
            {
                _upTickStreakBySymbol[symbol] = 0;
            }

            _lastPriceSampleBySymbol[symbol] = new PriceSample(currentPrice, observedAt);
            var strongMomentum = accelerationPerSecond >= MomentumAccelerationThresholdPerSecond && repeatedUpTicks;
            return new MomentumMetrics(accelerationPerSecond, repeatedUpTicks, strongMomentum);
        }
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
                RecordSignalOccurrence(signal.Symbol, signal.Timestamp == default ? now : signal.Timestamp, now);
            }

            foreach (var signal in currentSignals)
            {
                _recentSignals.RemoveAll(existing => existing.Symbol.Equals(signal.Symbol, StringComparison.Ordinal));
                _recentSignals.Insert(0, signal);
            }

            var cutoff = now.AddMinutes(-10);
            _recentSignals.RemoveAll(signal => signal.Timestamp < cutoff);
            if (_recentSignals.Count > 120)
            {
                _recentSignals.RemoveRange(120, _recentSignals.Count - 120);
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

    private decimal UpdateAndGetRelativeVolume(string symbol, decimal currentVolume)
    {
        if (currentVolume <= 0)
        {
            return 0m;
        }

        lock (_stateGate)
        {
            if (!_recentVolumeBySymbol.TryGetValue(symbol, out var history))
            {
                history = new Queue<decimal>();
                _recentVolumeBySymbol[symbol] = history;
            }

            var average = history.Count == 0 ? currentVolume : history.Average();
            history.Enqueue(currentVolume);
            while (history.Count > 12)
            {
                history.Dequeue();
            }

            if (average <= 0)
            {
                return 1m;
            }

            return Math.Clamp(currentVolume / average, 0m, 10m);
        }
    }

    private decimal GetPreviousChange(string symbol)
    {
        lock (_stateGate)
        {
            return _lastChangePercentBySymbol.GetValueOrDefault(symbol, 0m);
        }
    }

    private void SetPreviousChange(string symbol, decimal changePercent)
    {
        lock (_stateGate)
        {
            _lastChangePercentBySymbol[symbol] = changePercent;
        }
    }

    private bool IsRepeatedMomentum(string symbol, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            var count = GetSignalOccurrenceCount(symbol, now);
            return count >= TrendingOccurrenceThreshold;
        }
    }

    private void RecordSignalOccurrence(string symbol, DateTimeOffset timestamp, DateTimeOffset now)
    {
        _recentSignalOccurrences.Add(new SignalOccurrence(symbol, timestamp));
        PruneSignalOccurrences(now);
    }

    private int GetSignalOccurrenceCount(string symbol, DateTimeOffset now)
    {
        PruneSignalOccurrences(now);
        return _recentSignalOccurrences.Count(occurrence =>
            occurrence.Symbol.Equals(symbol, StringComparison.Ordinal) &&
            occurrence.Timestamp >= now - TrendingSymbolWindow);
    }

    private void PruneSignalOccurrences(DateTimeOffset now)
    {
        var cutoff = now - TrendingSymbolWindow;
        _recentSignalOccurrences.RemoveAll(occurrence => occurrence.Timestamp < cutoff);
        if (_recentSignalOccurrences.Count <= TrendingSignalMemory)
        {
            return;
        }

        _recentSignalOccurrences.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
        _recentSignalOccurrences.RemoveRange(TrendingSignalMemory, _recentSignalOccurrences.Count - TrendingSignalMemory);
    }

    private async Task<string> ResolveIndexDirectionAsync(DateTimeOffset now)
    {
        if (now - _marketContextCache.CachedAt < MarketContextCacheWindow)
        {
            return _marketContextCache.IndexDirection;
        }

        var symbols = new[] { "SPY", "QQQ" };
        var changes = new List<decimal>();
        foreach (var symbol in symbols)
        {
            var quote = await _finnhubService.GetQuoteAsync(symbol);
            if (quote is null || quote.PreviousClose <= 0 || quote.CurrentPrice <= 0)
            {
                continue;
            }

            var changePercent = ((quote.CurrentPrice - quote.PreviousClose) / quote.PreviousClose) * 100m;
            changes.Add(changePercent);
        }

        var direction = "FLAT";
        if (changes.Count > 0)
        {
            var average = changes.Average();
            direction = average > 0.4m ? "BULLISH" : average < -0.4m ? "BEARISH" : "FLAT";
        }

        _marketContextCache = (now, direction);
        return direction;
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

    private static string? ResolveSignalType(
        decimal changePercent,
        bool bullishNews,
        bool bearishNews,
        bool hasNews,
        bool isTrending,
        bool volumeSpike)
    {
        if (changePercent > SpikeThreshold)
        {
            return "SPIKE";
        }

        if (changePercent < BearishThreshold)
        {
            return "BEARISH";
        }

        if (changePercent >= BullishThreshold)
        {
            return "BULLISH";
        }

        if (changePercent <= -BullishThreshold)
        {
            return "BEARISH";
        }

        if (isTrending && volumeSpike)
        {
            return "TRENDING";
        }

        if (bullishNews || bearishNews || hasNews)
        {
            return "NEWS";
        }

        return null;
    }

    private static decimal CalculateCompositeScore(
        decimal changePercent,
        bool volumeSpike,
        bool bullishNews,
        bool bearishNews,
        bool hasNews,
        bool isTrending,
        bool isAccelerating,
        bool strongMomentum,
        bool highPriorityNews,
        bool lowPriorityNews,
        bool positiveNewsWithRisingPrice,
        decimal gapPercent,
        bool marketClosed,
        bool prioritizeNews)
    {
        var score = Math.Abs(changePercent) * 15m;

        if (volumeSpike)
        {
            score += 40m;
        }

        if (bullishNews)
        {
            score += 25m;
        }

        if (bearishNews)
        {
            score += 25m;
        }

        if (prioritizeNews && hasNews)
        {
            score += 20m;
        }

        if (isTrending)
        {
            score += 20m;
        }

        if (isAccelerating)
        {
            score += 20m;
        }

        if (strongMomentum)
        {
            score += 30m;
        }

        if (highPriorityNews)
        {
            score += 30m;
        }

        if (lowPriorityNews)
        {
            score -= 10m;
        }

        if (positiveNewsWithRisingPrice)
        {
            score += 15m;
        }

        if (Math.Abs(gapPercent) >= 1.5m)
        {
            score += 10m;
        }

        if (marketClosed)
        {
            score -= 15m;
        }

        return Math.Round(Math.Max(score, 0m), 2);
    }

    private static int CountConfirmations(
        bool volumeSpike,
        bool impactfulNews,
        bool accelerating,
        bool trending,
        bool gapping,
        bool meaningfulMove)
    {
        var confirmations = 0;
        if (meaningfulMove)
        {
            confirmations++;
        }

        if (volumeSpike)
        {
            confirmations++;
        }

        if (impactfulNews)
        {
            confirmations++;
        }

        if (accelerating)
        {
            confirmations++;
        }

        if (trending)
        {
            confirmations++;
        }

        if (gapping)
        {
            confirmations++;
        }

        return confirmations;
    }

    private static int CountCoreFactors(
        bool hasPriceMove,
        bool volumeSpike,
        bool hasNews,
        bool hasMomentum)
    {
        var factors = 0;
        if (hasPriceMove)
        {
            factors++;
        }

        if (volumeSpike)
        {
            factors++;
        }

        if (hasNews)
        {
            factors++;
        }

        if (hasMomentum)
        {
            factors++;
        }

        return factors;
    }

    private static bool PassNoiseGate(
        string signalType,
        decimal score,
        int confirmations,
        bool hasNews,
        decimal absChangePercent,
        bool volumeSpike,
        SessionTuning sessionTuning)
    {
        if (sessionTuning.RequireVolumeSpike && !volumeSpike)
        {
            return false;
        }

        if (signalType == "SPIKE" && score >= sessionTuning.ScoreThreshold)
        {
            return true;
        }

        if (signalType == "NEWS" && hasNews && score >= sessionTuning.ScoreThreshold)
        {
            return true;
        }

        if (sessionTuning.PrioritizeNews && hasNews && score >= sessionTuning.ScoreThreshold)
        {
            return true;
        }

        if (confirmations >= 2 && score >= sessionTuning.ScoreThreshold)
        {
            return true;
        }

        return absChangePercent >= 4m && score >= sessionTuning.ScoreThreshold;
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

    private static bool IsSentimentAligned(string signalType, bool bullishNews, bool bearishNews)
    {
        return signalType switch
        {
            "SPIKE" or "BULLISH" => bullishNews,
            "BEARISH" => bearishNews,
            "NEWS" => bullishNews || bearishNews,
            "TRENDING" => bullishNews || bearishNews,
            _ => false
        };
    }

    private static string ResolveTradeReadiness(decimal score, bool volumeSpike, bool sentimentAligned)
    {
        return score > 100m && volumeSpike && sentimentAligned
            ? "READY"
            : "WATCH";
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
        return NormalizeSentimentLabel(sentiment) switch
        {
            "BULLISH" => 0.6m,
            "BEARISH" => -0.6m,
            _ => 0m
        };
    }

    private static bool IsHighPriorityNews(NormalizedNewsItem? news)
    {
        if (news is null)
        {
            return false;
        }

        var haystack = $"{news.Category} {news.Headline}";
        return HighPriorityNewsKeywords.Any(keyword =>
            haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLowPriorityNews(NormalizedNewsItem? news)
    {
        if (news is null)
        {
            return false;
        }

        var haystack = $"{news.Category} {news.Headline}";
        return LowPriorityNewsKeywords.Any(keyword =>
            haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
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
            "TRENDING" => $"{symbol} is showing repeated momentum and high activity.",
            _ => $"{symbol} is active with a {changePercent:+0.##;-0.##;0}% move."
        };
    }

    private static string BuildReason(
        decimal changePercent,
        decimal gapPercent,
        decimal score,
        bool volumeSpike,
        decimal relativeVolume,
        bool isAccelerating,
        bool strongMomentum,
        bool isTrending,
        bool bullishNews,
        bool bearishNews,
        bool highPriorityNews,
        string marketSession,
        string indexDirection,
        NormalizedNewsItem? latestNews)
    {
        var weightedFactors = new List<(int Rank, string Text)>();

        if (isAccelerating || isTrending || strongMomentum)
        {
            var momentumLabel = strongMomentum
                ? "Breakout momentum"
                : isAccelerating && isTrending
                ? "Momentum breakout"
                : isAccelerating
                    ? "Momentum acceleration"
                    : "Repeated momentum";
            weightedFactors.Add((3, $"{momentumLabel} (HIGH)"));
        }

        if (volumeSpike)
        {
            weightedFactors.Add((3, $"Volume spike ({relativeVolume:0.##}x) (HIGH)"));
        }

        if (bullishNews)
        {
            weightedFactors.Add((2, "Positive news (MEDIUM)"));
        }
        else if (bearishNews)
        {
            weightedFactors.Add((2, "Negative news (MEDIUM)"));
        }
        else if (latestNews is not null)
        {
            weightedFactors.Add((2, "News flow (MEDIUM)"));
        }

        if (latestNews is not null && !string.IsNullOrWhiteSpace(latestNews.Category))
        {
            weightedFactors.Add((1, $"News category: {latestNews.Category} (LOW)"));
        }

        if (highPriorityNews)
        {
            weightedFactors.Add((3, "High-priority news catalyst (HIGH)"));
        }

        if (Math.Abs(changePercent) >= 0.01m)
        {
            weightedFactors.Add((1, $"Price move {changePercent:+0.##;-0.##;0}% (LOW)"));
        }

        if (Math.Abs(gapPercent) >= 0.01m)
        {
            weightedFactors.Add((1, $"Gap {gapPercent:+0.##;-0.##;0}% (LOW)"));
        }

        var ordered = weightedFactors
            .OrderByDescending(factor => factor.Rank)
            .Select(factor => factor.Text)
            .ToList();

        ordered.Add($"Session {marketSession}");
        ordered.Add($"Index {indexDirection}");
        ordered.Add($"Score {score:0.##}");
        return string.Join(" + ", ordered);
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
            PruneSignalOccurrences(now);
            foreach (var signal in signals)
            {
                var repeats = GetSignalOccurrenceCount(signal.Symbol, now) + 1;
                signal.RepeatCount = repeats;
                if (repeats >= TrendingOccurrenceThreshold)
                {
                    signal.IsTrending = true;
                    signal.Score = Math.Round(signal.Score + 20m, 2);
                    signal.ActivityScore = Math.Round(signal.ActivityScore + 20m, 2);
                    if (signal.SignalType is not "SPIKE" and not "BEARISH" and not "BULLISH")
                    {
                        signal.SignalType = "TRENDING";
                    }
                }
            }
        }
    }

    private async Task EnrichSignalsWithAdvancedFactorsAsync(
        IReadOnlyList<StockSignal> signals,
        CancellationToken cancellationToken)
    {
        foreach (var signal in signals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fundamentals = await GetFundamentalsSnapshotAsync(signal.Symbol, cancellationToken);
            if (fundamentals is not null)
            {
                signal.FloatShares = fundamentals.FloatShares;
                signal.InstitutionalOwnership = fundamentals.InstitutionalOwnership;
                signal.MarketCap = fundamentals.MarketCap;
            }

            signal.Flags = BuildAdvancedFlags(
                signal.ChangePercent,
                volumeSpike: (signal.VolumeRatio ?? 0m) >= RelativeVolumeSpikeThreshold,
                relativeVolume: signal.VolumeRatio ?? 0m,
                bullishNews: string.Equals(signal.Sentiment, "BULLISH", StringComparison.Ordinal),
                bearishNews: string.Equals(signal.Sentiment, "BEARISH", StringComparison.Ordinal),
                isTrending: signal.IsTrending || signal.SignalType == "TRENDING",
                floatShares: signal.FloatShares);
        }
    }

    private async Task<FundamentalsSnapshot?> GetFundamentalsSnapshotAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_stateGate)
        {
            if (_fundamentalsCache.TryGetValue(symbol, out var cached) &&
                now - cached.CachedAt < FundamentalsCacheWindow)
            {
                return cached;
            }
        }

        var profile = await _finnhubService.GetCompanyProfileAsync(symbol);
        var financials = await _finnhubService.GetBasicFinancialsAsync(symbol);
        if (profile is null && financials is null)
        {
            return null;
        }

        var snapshot = new FundamentalsSnapshot
        {
            FloatShares = financials?.Metric?.ShareFloat,
            InstitutionalOwnership = financials?.Metric?.InstitutionOwnership,
            MarketCap = profile?.MarketCapitalization,
            CachedAt = now
        };

        lock (_stateGate)
        {
            _fundamentalsCache[symbol] = snapshot;
        }

        return snapshot;
    }

    private static List<string> BuildAdvancedFlags(
        decimal changePercent,
        bool volumeSpike,
        decimal relativeVolume,
        bool bullishNews,
        bool bearishNews,
        bool isTrending,
        decimal? floatShares)
    {
        var flags = new List<string>();

        if (volumeSpike && relativeVolume >= 2.5m && (floatShares is null || floatShares <= 30m))
        {
            flags.Add("HIGH_CTB");
        }

        if (bearishNews && changePercent <= -3m)
        {
            flags.Add("REG_SHO");
        }

        if ((bullishNews || isTrending) && changePercent >= 2m)
        {
            flags.Add("R_S");
        }

        return flags;
    }

    private static SessionTuning ResolveSessionTuning(string marketSession)
    {
        return marketSession switch
        {
            "PREMARKET" => new SessionTuning(
                ScoreThreshold + 20m,
                RequireVolumeSpike: true,
                PrioritizeNews: false,
                ReducedVolumeRequirement: false),
            "AFTER_HOURS" => new SessionTuning(
                ScoreThreshold,
                RequireVolumeSpike: false,
                PrioritizeNews: true,
                ReducedVolumeRequirement: true),
            "REGULAR" => new SessionTuning(
                ScoreThreshold,
                RequireVolumeSpike: false,
                PrioritizeNews: false,
                ReducedVolumeRequirement: false),
            _ => new SessionTuning(
                ScoreThreshold,
                RequireVolumeSpike: false,
                PrioritizeNews: true,
                ReducedVolumeRequirement: true)
        };
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

    private sealed record PriceSample(decimal Price, DateTimeOffset Timestamp);

    private sealed record SignalOccurrence(string Symbol, DateTimeOffset Timestamp);

    private sealed record MomentumMetrics(
        decimal AccelerationPerSecond,
        bool RepeatedUpTicks,
        bool StrongMomentum);

    private sealed record SessionTuning(
        decimal ScoreThreshold,
        bool RequireVolumeSpike,
        bool PrioritizeNews,
        bool ReducedVolumeRequirement);

    private sealed class FundamentalsSnapshot
    {
        public decimal? FloatShares { get; init; }

        public decimal? InstitutionalOwnership { get; init; }

        public decimal? MarketCap { get; init; }

        public DateTimeOffset CachedAt { get; init; }
    }
}
