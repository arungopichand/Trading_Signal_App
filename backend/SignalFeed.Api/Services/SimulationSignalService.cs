using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class SimulationSignalService
{
    private enum MarketPhase
    {
        Quiet,
        Burst
    }

    private enum SignalProfile
    {
        Low,
        Medium,
        High
    }

    private const int MinCycleInputs = 3;
    private const int MaxCycleInputs = 5;
    private const int MinInsertedPerCycle = 2;

    private static readonly string[] Symbols = ["AAPL", "TSLA", "NVDA", "AMD", "META", "PLTR", "MSFT", "AMZN", "GOOGL", "NFLX"];
    private static readonly string[] TrendingSymbols = ["NVDA", "TSLA"];
    private static readonly string[] BullishHeadlines =
    [
        "beats earnings expectations",
        "wins major enterprise contract",
        "receives analyst upgrade",
        "raises guidance on strong demand",
        "announces breakout product momentum"
    ];

    private static readonly string[] BearishHeadlines =
    [
        "misses earnings expectations",
        "announces secondary offering",
        "receives analyst downgrade",
        "cuts guidance amid weak demand",
        "faces legal overhang in latest update"
    ];

    private static readonly string[] Sources = ["Reuters", "Bloomberg", "CNBC", "MarketWatch"];
    private static readonly string[] Categories = ["earnings", "analyst", "product", "contract", "legal", "market commentary"];

    private readonly SignalEngine _signalEngine;
    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _lastPriceBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _trendStrengthBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _trendDirectionBySymbol = new(StringComparer.Ordinal);
    private readonly Queue<SimulationStatPoint> _statsWindow = new();
    private decimal _equity = 100_000m;
    private decimal _peakEquity = 100_000m;
    private decimal _maxDrawdownPercent;
    private int _tradeCount;
    private int _winCount;
    private decimal _netPnl;

    private MarketPhase _phase = MarketPhase.Quiet;
    private DateTimeOffset _phaseEndsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSpikeAt = DateTimeOffset.MinValue;
    private int _fallbackSymbolOffset;

    public SimulationSignalService(SignalEngine signalEngine)
    {
        _signalEngine = signalEngine;
    }

    public async Task<IReadOnlyList<FeedItem>> GenerateFeedBatchAsync(CancellationToken cancellationToken = default)
    {
        List<SimulatedMarketInput> inputs;
        DateTimeOffset now;
        var forceStrongGuarantee = false;

        lock (_gate)
        {
            now = DateTimeOffset.UtcNow;
            InitializeScheduleIfNeeded(now);
            AdvancePhaseIfNeeded(now);

            forceStrongGuarantee = now >= _nextSpikeAt;
            if (forceStrongGuarantee)
            {
                _nextSpikeAt = now.AddSeconds(_random.Next(10, 16));
            }

            var inputCount = _random.Next(MinCycleInputs, MaxCycleInputs + 1);
            inputs = BuildInputs(now, inputCount, forceStrongGuarantee);
        }

        var signals = await _signalEngine.GenerateSignalsFromInputsAsync(inputs, scoreThresholdOverride: 40m, cancellationToken);
        var feedItems = signals
            .Select(ToFeedItem)
            .ToList();

        if (forceStrongGuarantee && !feedItems.Any(item => item.SignalType == "SPIKE" || item.Score >= 90m))
        {
            feedItems.Insert(0, CreateFallbackSignal(GetNextFallbackSymbol(), now, strong: true));
        }

        while (feedItems.Count < MinInsertedPerCycle)
        {
            feedItems.Add(CreateFallbackSignal(GetNextFallbackSymbol(), now, strong: false));
        }

        var fallbackCount = Math.Max(0, feedItems.Count - signals.Count);
        RecordStats(now, signals.Count, fallbackCount, feedItems.Count);
        RecordPerformance(feedItems);

        return feedItems
            .OrderByDescending(item => item.IsTopOpportunity)
            .ThenByDescending(item => item.Score > 0 ? item.Score : item.ActivityScore)
            .ThenByDescending(item => item.Timestamp)
            .Take(MaxCycleInputs)
            .ToList();
    }

    public SimulationStatsSnapshot GetStatsSnapshot()
    {
        lock (_gate)
        {
            PruneOldStats(DateTimeOffset.UtcNow);
            var engine = _statsWindow.Sum(point => point.EngineCount);
            var fallback = _statsWindow.Sum(point => point.FallbackCount);
            var total = _statsWindow.Sum(point => point.TotalCount);
            var fallbackRate = total == 0
                ? 0m
                : Math.Round((fallback / (decimal)total) * 100m, 2);

            return new SimulationStatsSnapshot
            {
                EngineSignalsPerMinute = engine,
                FallbackSignalsPerMinute = fallback,
                TotalSignalsPerMinute = total,
                FallbackRatePercent = fallbackRate
            };
        }
    }

    public SimulationPerformanceSnapshot GetPerformanceSnapshot()
    {
        lock (_gate)
        {
            var winRate = _tradeCount > 0
                ? Math.Round((_winCount / (decimal)_tradeCount) * 100m, 2)
                : 0m;
            var expectancy = _tradeCount > 0
                ? Math.Round(_netPnl / _tradeCount, 2)
                : 0m;
            var currentDrawdown = _peakEquity > 0m
                ? Math.Round(((_peakEquity - _equity) / _peakEquity) * 100m, 2)
                : 0m;

            return new SimulationPerformanceSnapshot
            {
                TradeCount = _tradeCount,
                WinCount = _winCount,
                WinRatePercent = winRate,
                NetPnl = Math.Round(_netPnl, 2),
                ExpectancyPerTrade = expectancy,
                CurrentEquity = Math.Round(_equity, 2),
                PeakEquity = Math.Round(_peakEquity, 2),
                CurrentDrawdownPercent = currentDrawdown,
                MaxDrawdownPercent = Math.Round(_maxDrawdownPercent, 2)
            };
        }
    }

    private List<SimulatedMarketInput> BuildInputs(DateTimeOffset now, int count, bool includeForcedStrong)
    {
        var output = new List<SimulatedMarketInput>(count);
        var spikeIndex = includeForcedStrong ? _random.Next(count) : -1;

        for (var i = 0; i < count; i++)
        {
            var forceSpike = i == spikeIndex;
            var symbol = SelectSymbol();
            var isTrendingSymbol = TrendingSymbols.Contains(symbol, StringComparer.Ordinal);
            var trendStrength = UpdateTrendStrength(symbol, isTrendingSymbol);
            var direction = ResolveTrendDirection(symbol, isTrendingSymbol);

            var profile = forceSpike
                ? SignalProfile.High
                : SelectProfile(_phase);

            var previousClose = GetPreviousClose(symbol);
            var (changePercent, volume, sentiment) = ResolveMove(profile, trendStrength, direction, forceSpike);

            var currentPrice = Math.Round(previousClose * (1m + (changePercent / 100m)), 2);
            _lastPriceBySymbol[symbol] = currentPrice;

            var gapPercent = ResolveGapPercent(changePercent, profile, forceSpike);
            var openPrice = Math.Round(previousClose * (1m + (gapPercent / 100m)), 2);
            var high = Math.Round(Math.Max(currentPrice, openPrice) * (1m + (decimal)_random.NextDouble() * 0.012m), 2);
            var low = Math.Round(Math.Min(currentPrice, openPrice) * (1m - (decimal)_random.NextDouble() * 0.012m), 2);

            var hasNews = forceSpike || _random.NextDouble() < (_phase == MarketPhase.Burst ? 0.30d : 0.14d);
            var category = Categories[_random.Next(Categories.Length)];
            var headline = hasNews ? BuildHeadline(symbol, sentiment) : string.Empty;
            var source = hasNews ? Sources[_random.Next(Sources.Length)] : "SIM";
            var url = hasNews ? $"https://example.com/news/{symbol.ToLowerInvariant()}-{now.ToUnixTimeSeconds()}" : string.Empty;

            output.Add(new SimulatedMarketInput
            {
                Symbol = symbol,
                PreviousClose = previousClose,
                CurrentPrice = currentPrice,
                OpenPrice = openPrice,
                High = high,
                Low = low,
                Volume = Math.Round(volume, 0),
                Headline = headline,
                Source = source,
                Url = url,
                Sentiment = sentiment,
                Category = category,
                Timestamp = now.AddMilliseconds(-_random.Next(0, 850))
            });
        }

        return output;
    }

    private SignalProfile SelectProfile(MarketPhase phase)
    {
        var roll = _random.NextDouble();
        if (phase == MarketPhase.Burst)
        {
            if (roll < 0.12d)
            {
                return SignalProfile.High;
            }

            if (roll < 0.42d)
            {
                return SignalProfile.Medium;
            }

            return SignalProfile.Low;
        }

        // Quiet phase still emits data but skewed lower intensity.
        if (roll < 0.05d)
        {
            return SignalProfile.High;
        }

        if (roll < 0.30d)
        {
            return SignalProfile.Medium;
        }

        return SignalProfile.Low;
    }

    private string SelectSymbol()
    {
        var useTrending = _random.NextDouble() < 0.40d;
        if (useTrending)
        {
            return TrendingSymbols[_random.Next(TrendingSymbols.Length)];
        }

        return Symbols[_random.Next(Symbols.Length)];
    }

    private int UpdateTrendStrength(string symbol, bool isTrending)
    {
        if (!_trendStrengthBySymbol.TryGetValue(symbol, out var strength))
        {
            strength = 0;
        }

        if (isTrending)
        {
            strength = Math.Min(10, strength + 1);
        }
        else
        {
            strength = Math.Max(0, strength - 1);
        }

        _trendStrengthBySymbol[symbol] = strength;
        return strength;
    }

    private int ResolveTrendDirection(string symbol, bool isTrending)
    {
        if (!_trendDirectionBySymbol.TryGetValue(symbol, out var direction))
        {
            direction = _random.NextDouble() < 0.6d ? 1 : -1;
        }

        if (isTrending && _random.NextDouble() < 0.08d)
        {
            direction *= -1;
        }

        _trendDirectionBySymbol[symbol] = direction;
        return direction;
    }

    private (decimal ChangePercent, decimal Volume, string Sentiment) ResolveMove(
        SignalProfile profile,
        int trendStrength,
        int trendDirection,
        bool forceSpike)
    {
        decimal baseMin;
        decimal baseMax;
        decimal volumeMin;
        decimal volumeMax;

        if (forceSpike || profile == SignalProfile.High)
        {
            baseMin = 3m;
            baseMax = 5m;
            volumeMin = 1_500_000m;
            volumeMax = 4_500_000m;
        }
        else if (profile == SignalProfile.Medium)
        {
            baseMin = 1.1m;
            baseMax = 2.8m;
            volumeMin = 320_000m;
            volumeMax = 1_550_000m;
        }
        else
        {
            baseMin = 0.2m;
            baseMax = 1.2m;
            volumeMin = 90_000m;
            volumeMax = 500_000m;
        }

        var trendBoost = trendStrength * 0.18m;
        var magnitude = baseMin + ((decimal)_random.NextDouble() * (baseMax - baseMin));
        magnitude += trendBoost;

        var directionalBias = trendDirection >= 0 ? 1m : -1m;
        var finalDirection = _random.NextDouble() < 0.82d ? directionalBias : -directionalBias;
        var changePercent = Math.Round(magnitude * finalDirection, 2);

        var volume = volumeMin + ((decimal)_random.NextDouble() * (volumeMax - volumeMin));
        volume *= 1m + Math.Min(0.55m, trendStrength * 0.07m);

        var sentiment = "NEUTRAL";
        if (forceSpike || profile != SignalProfile.Low || _random.NextDouble() < 0.35d)
        {
            sentiment = changePercent >= 0 ? "BULLISH" : "BEARISH";
        }

        return (changePercent, volume, sentiment);
    }

    private decimal ResolveGapPercent(decimal changePercent, SignalProfile profile, bool forceSpike)
    {
        var baseGap = forceSpike || profile == SignalProfile.High
            ? RandomSignedRange(1.0m, 2.5m)
            : RandomSignedRange(0m, 1.6m);
        var alignedGap = Math.Sign(changePercent) * Math.Abs(baseGap);
        return Math.Round(_random.NextDouble() < 0.75d ? alignedGap : baseGap, 2);
    }

    private decimal GetPreviousClose(string symbol)
    {
        if (_lastPriceBySymbol.TryGetValue(symbol, out var previous))
        {
            return previous;
        }

        var seeded = Math.Round(45m + (decimal)_random.NextDouble() * 420m, 2);
        _lastPriceBySymbol[symbol] = seeded;
        return seeded;
    }

    private FeedItem CreateFallbackSignal(string symbol, DateTimeOffset now, bool strong)
    {
        var price = Math.Round(100m + ((decimal)_random.NextDouble() * 400m), 2);
        var changePercent = strong
            ? Math.Round(3m + ((decimal)_random.NextDouble() * 2m), 2)
            : Math.Round(RandomSignedRange(0.5m, 2m), 2);

        var score = strong
            ? Math.Round(90m + ((decimal)_random.NextDouble() * 30m), 2)
            : Math.Round(60m + ((decimal)_random.NextDouble() * 18m), 2);

        var signalType = strong ? "SPIKE" : changePercent >= 0 ? "BULLISH" : "BEARISH";
        var sentiment = changePercent >= 0 ? "BULLISH" : "BEARISH";

        return new FeedItem
        {
            Symbol = symbol,
            CountryCode = "US",
            Price = price,
            PriceRange = PriceRangeResolver.GetPriceRange(price),
            ChangePercent = changePercent,
            SignalType = signalType,
            Score = score,
            ActivityScore = score,
            Confidence = strong ? "HIGH" : "LOW",
            TradeReadiness = "WATCH",
            IsTopOpportunity = false,
            IsTrending = false,
            Headline = strong
                ? $"{symbol} simulated spike event in rhythm engine"
                : $"{symbol} simulated market movement",
            Url = string.Empty,
            Reason = "Simulated market movement",
            FloatShares = Math.Round(4m + ((decimal)_random.NextDouble() * 75m), 2),
            InstitutionalOwnership = Math.Round(8m + ((decimal)_random.NextDouble() * 84m), 2),
            MarketCap = Math.Round(250m + ((decimal)_random.NextDouble() * 150_000m), 2),
            Volume = Math.Round(80_000m + ((decimal)_random.NextDouble() * 3_800_000m), 0),
            Flags = strong
                ? ["R_S", "HIGH_CTB"]
                : [],
            VolumeRatio = strong ? Math.Round(2.4m + ((decimal)_random.NextDouble() * 1.8m), 2) : Math.Round(0.9m + ((decimal)_random.NextDouble() * 1.4m), 2),
            Momentum = changePercent,
            Sentiment = sentiment,
            Acceleration = Math.Round(RandomSignedRange(0.1m, 1.5m), 2),
            GapPercent = Math.Round(RandomSignedRange(0m, 1.5m), 2),
            NewsCategory = string.Empty,
            RepeatCount = 1,
            Timestamp = now,
            Source = "SIM"
        };
    }

    private string GetNextFallbackSymbol()
    {
        var symbol = Symbols[_fallbackSymbolOffset % Symbols.Length];
        _fallbackSymbolOffset = (_fallbackSymbolOffset + 1) % Symbols.Length;
        return symbol;
    }

    private static FeedItem ToFeedItem(StockSignal signal)
    {
        return new FeedItem
        {
            Symbol = signal.Symbol,
            CountryCode = string.IsNullOrWhiteSpace(signal.CountryCode) ? "US" : signal.CountryCode,
            Price = signal.Price,
            PriceRange = string.IsNullOrWhiteSpace(signal.PriceRange)
                ? PriceRangeResolver.GetPriceRange(signal.Price)
                : signal.PriceRange,
            ChangePercent = signal.ChangePercent,
            SignalType = signal.SignalType,
            Score = signal.Score,
            ActivityScore = signal.ActivityScore,
            Confidence = signal.Confidence,
            TradeReadiness = signal.TradeReadiness,
            IsTopOpportunity = signal.IsTopOpportunity,
            IsTrending = signal.IsTrending,
            Headline = signal.Headline,
            Url = signal.Url,
            Reason = signal.SignalReason,
            FloatShares = signal.FloatShares,
            InstitutionalOwnership = signal.InstitutionalOwnership,
            MarketCap = signal.MarketCap,
            Volume = signal.Volume,
            Flags = signal.Flags ?? [],
            VolumeRatio = signal.VolumeRatio,
            Momentum = signal.Momentum,
            Sentiment = signal.Sentiment,
            Acceleration = signal.Acceleration,
            GapPercent = signal.GapPercent,
            NewsCategory = signal.NewsCategory,
            RepeatCount = signal.RepeatCount,
            Timestamp = signal.Timestamp == default ? signal.ScannedAt : signal.Timestamp,
            Source = string.IsNullOrWhiteSpace(signal.Source) ? "SIM" : signal.Source
        };
    }

    private string BuildHeadline(string symbol, string sentiment)
    {
        if (sentiment == "BULLISH")
        {
            return $"{symbol} {BullishHeadlines[_random.Next(BullishHeadlines.Length)]}";
        }

        if (sentiment == "BEARISH")
        {
            return $"{symbol} {BearishHeadlines[_random.Next(BearishHeadlines.Length)]}";
        }

        return $"{symbol} trades quietly as market participants digest flows";
    }

    private decimal RandomSignedRange(decimal minAbs, decimal maxAbs)
    {
        var magnitude = minAbs + ((decimal)_random.NextDouble() * (maxAbs - minAbs));
        return _random.NextDouble() < 0.5d ? -magnitude : magnitude;
    }

    private void InitializeScheduleIfNeeded(DateTimeOffset now)
    {
        if (_phaseEndsAt != DateTimeOffset.MinValue)
        {
            return;
        }

        _phase = MarketPhase.Quiet;
        _phaseEndsAt = now.AddSeconds(_random.Next(2, 5));
        _nextSpikeAt = now.AddSeconds(_random.Next(10, 16));
    }

    private void AdvancePhaseIfNeeded(DateTimeOffset now)
    {
        if (now < _phaseEndsAt)
        {
            return;
        }

        _phase = _phase == MarketPhase.Quiet ? MarketPhase.Burst : MarketPhase.Quiet;
        _phaseEndsAt = _phase == MarketPhase.Quiet
            ? now.AddSeconds(_random.Next(2, 5))
            : now.AddSeconds(_random.Next(1, 3));
    }

    private void RecordStats(DateTimeOffset now, int engineCount, int fallbackCount, int totalCount)
    {
        lock (_gate)
        {
            _statsWindow.Enqueue(new SimulationStatPoint(now, engineCount, fallbackCount, totalCount));
            PruneOldStats(now);
        }
    }

    private void RecordPerformance(IReadOnlyList<FeedItem> feedItems)
    {
        const decimal positionSize = 10_000m;
        const decimal slippageRate = 0.0005m;
        const decimal feePerTrade = 2m;

        var tradable = feedItems
            .Where(item =>
                item.SignalType.Equals("SPIKE", StringComparison.OrdinalIgnoreCase) ||
                item.SignalType.Equals("BULLISH", StringComparison.OrdinalIgnoreCase) ||
                item.SignalType.Equals("BEARISH", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();

        if (tradable.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var item in tradable)
            {
                var direction = item.SignalType.Equals("BEARISH", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
                var grossPnl = direction * (item.ChangePercent / 100m) * positionSize;
                var slippage = positionSize * slippageRate;
                var netPnl = grossPnl - slippage - feePerTrade;

                _tradeCount++;
                if (netPnl > 0m)
                {
                    _winCount++;
                }

                _netPnl += netPnl;
                _equity += netPnl;
                if (_equity > _peakEquity)
                {
                    _peakEquity = _equity;
                }

                if (_peakEquity > 0m)
                {
                    var drawdownPercent = ((_peakEquity - _equity) / _peakEquity) * 100m;
                    _maxDrawdownPercent = Math.Max(_maxDrawdownPercent, drawdownPercent);
                }
            }
        }
    }

    private void PruneOldStats(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-1);
        while (_statsWindow.Count > 0 && _statsWindow.Peek().Timestamp < cutoff)
        {
            _statsWindow.Dequeue();
        }
    }

    private sealed record SimulationStatPoint(
        DateTimeOffset Timestamp,
        int EngineCount,
        int FallbackCount,
        int TotalCount);
}
