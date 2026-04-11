using Microsoft.AspNetCore.SignalR;
using SignalFeed.Api.Hubs;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FeedService
{
    private const int MaxItems = 100;
    private static readonly TimeSpan ExpireWindow = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ScoreDecayStart = TimeSpan.FromSeconds(30);
    private const decimal MaxScoreDecay = 0.55m;
    private static readonly TimeSpan SymbolReemitWindow = TimeSpan.FromSeconds(30);
    private const decimal SymbolScoreJumpThreshold = 25m;
    private static readonly TimeSpan EmitSpacing = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan TrendingWindow = TimeSpan.FromMinutes(5);
    private const int TrendingThreshold = 3;
    private readonly IHubContext<FeedHub> _hubContext;
    private readonly ILogger<FeedService> _logger;
    private readonly List<FeedItem> _items = [];
    private readonly HashSet<string> _fingerprints = [];
    private readonly Dictionary<string, (DateTimeOffset EmittedAt, decimal Score, string SignalType)> _lastEmittedBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _emissionHistoryBySymbol = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _lastRealInsertAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEmitSlot = DateTimeOffset.MinValue;

    public FeedService(IHubContext<FeedHub> hubContext, ILogger<FeedService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public IReadOnlyList<FeedItem> GetLatest()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredItems(now);
            return _items
                .OrderByDescending(item => item.IsTopOpportunity)
                .ThenByDescending(item => GetDecayedScore(item, now))
                .ThenByDescending(item => item.Timestamp)
                .Take(MaxItems)
                .ToList();
        }
    }

    public bool IsRealDataStale(TimeSpan staleWindow)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return true;
            }

            return DateTimeOffset.UtcNow - _lastRealInsertAt > staleWindow;
        }
    }

    public IReadOnlyList<string> GetHotSymbols(int count)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredItems(now);
            return _items
                .Where(item => !string.IsNullOrWhiteSpace(item.Symbol))
                .OrderByDescending(item => item.IsTopOpportunity)
                .ThenByDescending(item => GetDecayedScore(item, now))
                .ThenByDescending(item => item.Timestamp)
                .Select(item => item.Symbol)
                .Distinct(StringComparer.Ordinal)
                .Take(Math.Clamp(count, 1, 100))
                .ToList();
        }
    }

    public Task AddSignalAsync(StockSignal signal, CancellationToken cancellationToken = default)
    {
        var feedItem = new FeedItem
        {
            Symbol = signal.Symbol,
            CountryCode = string.IsNullOrWhiteSpace(signal.CountryCode) ? "US" : signal.CountryCode.Trim().ToUpperInvariant(),
            Price = signal.Price,
            PriceRange = string.IsNullOrWhiteSpace(signal.PriceRange)
                ? PriceRangeResolver.GetPriceRange(signal.Price)
                : signal.PriceRange,
            ChangePercent = signal.ChangePercent,
            SignalType = signal.SignalType,
            Score = signal.Score > 0 ? signal.Score : signal.ActivityScore,
            ActivityScore = signal.ActivityScore,
            Confidence = signal.Confidence,
            TradeReadiness = string.IsNullOrWhiteSpace(signal.TradeReadiness) ? "WATCH" : signal.TradeReadiness,
            IsTopOpportunity = signal.IsTopOpportunity,
            IsTrending = signal.IsTrending,
            Headline = signal.Headline,
            Url = signal.Url,
            Reason = signal.SignalReason,
            Reasons = signal.Reasons ?? [],
            FloatShares = signal.FloatShares,
            InstitutionalOwnership = signal.InstitutionalOwnership,
            MarketCap = signal.MarketCap,
            Volume = signal.Volume,
            Flags = signal.Flags ?? [],
            VolumeRatio = signal.VolumeRatio,
            Momentum = signal.Momentum,
            Sentiment = NormalizeSentiment(signal.Sentiment),
            Acceleration = signal.Acceleration,
            GapPercent = signal.GapPercent,
            NewsCategory = signal.NewsCategory,
            RepeatCount = Math.Max(0, signal.RepeatCount),
            Timestamp = signal.Timestamp == default ? signal.ScannedAt : signal.Timestamp,
            MomentumDetectedAt = signal.MomentumDetectedAt,
            Source = string.IsNullOrWhiteSpace(signal.Source) ? "Scanner" : signal.Source
        };

        return AddItemAsync(feedItem, cancellationToken);
    }

    public Task AddNewsAsync(NormalizedNewsItem news, CancellationToken cancellationToken = default)
    {
        var type = news.Sentiment switch
        {
            "BULLISH" => "BULLISH",
            "BEARISH" => "BEARISH",
            _ => "NEWS"
        };
        var score = Math.Round(Math.Abs(news.SentimentScore) * 100m, 2);
        if (score < 70m)
        {
            return Task.CompletedTask;
        }

        var feedItem = new FeedItem
        {
            Symbol = news.Symbol,
            CountryCode = "US",
            SignalType = type,
            Score = score,
            ActivityScore = score,
            Confidence = NormalizeConfidence(string.Empty, score),
            TradeReadiness = "WATCH",
            IsTopOpportunity = false,
            IsTrending = false,
            Headline = news.Headline,
            Url = news.Url,
            Reason = $"News sentiment: {news.Sentiment} + Category: {news.Category} + Score {score:0.##}",
            Reasons = [$"News sentiment {news.Sentiment}", $"Category {news.Category}"],
            FloatShares = null,
            InstitutionalOwnership = null,
            MarketCap = null,
            Volume = null,
            Flags = [],
            Sentiment = NormalizeSentiment(news.Sentiment),
            NewsCategory = news.Category,
            RepeatCount = 1,
            Timestamp = news.Datetime == default ? DateTimeOffset.UtcNow : news.Datetime,
            MomentumDetectedAt = null,
            Source = string.IsNullOrWhiteSpace(news.Source) ? "NEWS" : news.Source
        };

        return AddItemAsync(feedItem, cancellationToken);
    }

    public async Task AddItemAsync(FeedItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.Symbol) || string.IsNullOrWhiteSpace(item.Headline))
        {
            return;
        }

        item.SignalType = NormalizeSignalType(item.SignalType);
        item.CountryCode = NormalizeCountryCode(item.CountryCode);
        item.Url = NormalizeUrl(item.Url);
        item.Score = item.Score > 0 ? item.Score : item.ActivityScore;
        item.PriceRange = string.IsNullOrWhiteSpace(item.PriceRange)
            ? PriceRangeResolver.GetPriceRange(item.Price)
            : item.PriceRange.Trim();
        item.Flags = item.Flags
            .Where(flag => !string.IsNullOrWhiteSpace(flag))
            .Select(flag => flag.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        item.Confidence = NormalizeConfidence(item.Confidence, item.Score);
        item.TradeReadiness = NormalizeTradeReadiness(item.TradeReadiness);
        item.Sentiment = NormalizeSentiment(item.Sentiment);
        item.NewsCategory = NormalizeNewsCategory(item.NewsCategory);
        item.RepeatCount = Math.Max(0, item.RepeatCount);
        var fingerprint = BuildFingerprint(item);
        var wasInserted = false;
        var totalItems = 0;
        var emitDelay = TimeSpan.Zero;

        lock (_gate)
        {
            PruneExpiredItems(DateTimeOffset.UtcNow);

            if (_lastEmittedBySymbol.TryGetValue(item.Symbol, out var lastEmission))
            {
                var withinQuietWindow = item.Timestamp - lastEmission.EmittedAt < SymbolReemitWindow;
                var scoreIncrease = item.Score - lastEmission.Score;
                var signalTypeChanged = !item.SignalType.Equals(lastEmission.SignalType, StringComparison.Ordinal);
                if (withinQuietWindow && scoreIncrease < SymbolScoreJumpThreshold && !signalTypeChanged)
                {
                    _logger.LogInformation(
                        "FeedService suppressed {Symbol} within {WindowSeconds}s (score +{ScoreIncrease:0.##}, signal type unchanged).",
                        item.Symbol,
                        SymbolReemitWindow.TotalSeconds,
                        scoreIncrease);
                    return;
                }
            }

            if (!_fingerprints.Add(fingerprint))
            {
                _logger.LogInformation("FeedService deduplicated item for {Symbol} ({SignalType}).", item.Symbol, item.SignalType);
                return;
            }

            if (item.IsTopOpportunity)
            {
                foreach (var existing in _items)
                {
                    existing.IsTopOpportunity = false;
                }
            }

            item.IsTrending = UpdateTrending(item.Symbol, item.Timestamp);
            _items.Insert(0, item);
            _lastEmittedBySymbol[item.Symbol] = (item.Timestamp, item.Score, item.SignalType);
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }

            if (_fingerprints.Count > 1000)
            {
                _fingerprints.Clear();
                foreach (var existing in _items.Take(200))
                {
                    _fingerprints.Add(BuildFingerprint(existing));
                }
            }

            _lastRealInsertAt = DateTimeOffset.UtcNow;
            var now = DateTimeOffset.UtcNow;
            var slot = _nextEmitSlot > now ? _nextEmitSlot : now;
            emitDelay = slot - now;
            _nextEmitSlot = slot + EmitSpacing;

            totalItems = _items.Count;
            wasInserted = true;
        }

        if (!wasInserted)
        {
            return;
        }

        try
        {
            if (emitDelay > TimeSpan.Zero)
            {
                await Task.Delay(emitDelay, cancellationToken);
            }

            _logger.LogInformation(
                "FeedService added item: Symbol={Symbol}, Type={SignalType}, Source={Source}, TotalItems={TotalItems}.",
                item.Symbol,
                item.SignalType,
                item.Source,
                totalItems);
            await _hubContext.Clients.All.SendAsync("newSignal", item, cancellationToken);
            _logger.LogInformation("FeedService broadcasted newSignal for {Symbol}.", item.Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR broadcast failed for {Symbol}.", item.Symbol);
        }
    }

    private static string BuildFingerprint(FeedItem item)
    {
        var timeBucket = item.Timestamp.ToUnixTimeSeconds() / 30;
        return $"{item.Symbol}|{item.SignalType}|{item.Headline}|{timeBucket}";
    }

    private static string NormalizeSignalType(string signalType)
    {
        var normalized = signalType?.Trim().ToUpperInvariant() ?? "NEWS";
        return normalized switch
        {
            "SPIKE" or "BULLISH" or "BEARISH" or "NEWS" or "TRENDING" or "TOP_OPPORTUNITY" => normalized,
            _ => "NEWS"
        };
    }

    private static string NormalizeConfidence(string confidence, decimal score)
    {
        if (!string.IsNullOrWhiteSpace(confidence))
        {
            var normalized = confidence.Trim().ToUpperInvariant();
            if (normalized is "HIGH" or "MEDIUM" or "LOW")
            {
                return normalized;
            }
        }

        if (score > 100m)
        {
            return "HIGH";
        }

        return score > 70m ? "MEDIUM" : "LOW";
    }

    private static string NormalizeTradeReadiness(string tradeReadiness)
    {
        var normalized = tradeReadiness?.Trim().ToUpperInvariant();
        return normalized == "READY" ? "READY" : "WATCH";
    }

    private static string NormalizeCountryCode(string countryCode)
    {
        var normalized = countryCode?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "US" : normalized;
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return url.Trim();
    }

    private static string NormalizeSentiment(string sentiment)
    {
        var normalized = sentiment?.Trim().ToUpperInvariant();
        return normalized switch
        {
            "BULLISH" => "BULLISH",
            "BEARISH" => "BEARISH",
            _ => "NEUTRAL"
        };
    }

    private static string NormalizeNewsCategory(string category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : category.Trim();
    }

    private bool UpdateTrending(string symbol, DateTimeOffset timestamp)
    {
        if (!_emissionHistoryBySymbol.TryGetValue(symbol, out var history))
        {
            history = new Queue<DateTimeOffset>();
            _emissionHistoryBySymbol[symbol] = history;
        }

        history.Enqueue(timestamp);
        var cutoff = timestamp - TrendingWindow;
        while (history.Count > 0 && history.Peek() < cutoff)
        {
            history.Dequeue();
        }

        return history.Count >= TrendingThreshold;
    }

    private void PruneExpiredItems(DateTimeOffset now)
    {
        _items.RemoveAll(item => now - item.Timestamp > ExpireWindow);
    }

    private static decimal GetDecayedScore(FeedItem item, DateTimeOffset now)
    {
        var baseScore = item.Score > 0 ? item.Score : item.ActivityScore;
        var age = now - item.Timestamp;
        if (age <= ScoreDecayStart)
        {
            return baseScore;
        }

        var decayProgress = (age - ScoreDecayStart).TotalSeconds / Math.Max(1d, (ExpireWindow - ScoreDecayStart).TotalSeconds);
        var bounded = Math.Clamp((decimal)decayProgress, 0m, 1m);
        var decayFactor = 1m - (MaxScoreDecay * bounded);
        return Math.Round(baseScore * decayFactor, 2);
    }
}
