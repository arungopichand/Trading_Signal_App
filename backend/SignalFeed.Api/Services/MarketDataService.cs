using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class MarketDataService : IMarketDataService
{
    private static readonly TimeSpan PriceTtl = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FundamentalsTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GlobalNewsTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan QuoteStaleAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TopOpportunityBypassWindow = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim GlobalApiGate = new(4, 4);
    private const int MaxLatencySamples = 2000;
    private static readonly object PolygonBudgetGate = new();
    private static readonly Queue<DateTimeOffset> PolygonBudgetWindow = new();
    private const int MaxPolygonCallsPerMinute = 4;

    private readonly FinnhubService _finnhubService;
    private readonly PolygonService _polygonService;
    private readonly ExternalNewsApiService _newsApiService;
    private readonly FmpService _fmpService;
    private readonly IFinnhubWebSocketService _quoteStream;
    private readonly ProviderHealthTracker _providerHealth;
    private readonly ApiUsageTracker _apiUsageTracker;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MarketDataService> _logger;
    private readonly object _latencyGate = new();
    private readonly Queue<long> _providerLatencyMsSamples = new();
    private readonly ConcurrentDictionary<string, UnifiedMarketData> _lastKnownBySymbol = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forceFreshSymbols = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<QuoteResponse?>>> _inflightQuotes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<FmpFactors?>>> _inflightFundamentals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NewsApiArticle>?>>> _inflightGlobalNews = new(StringComparer.Ordinal);

    private long _totalApiCalls;
    private long _successfulCalls;
    private long _failedCalls;
    private long _fallbackUsed;
    private long _errors;
    private long _cacheHits;
    private long _cacheMisses;
    private long _staleDataReturns;
    private long _totalQuoteResults;

    public MarketDataService(
        FinnhubService finnhubService,
        PolygonService polygonService,
        ExternalNewsApiService newsApiService,
        FmpService fmpService,
        IFinnhubWebSocketService quoteStream,
        ProviderHealthTracker providerHealth,
        ApiUsageTracker apiUsageTracker,
        IMemoryCache cache,
        ILogger<MarketDataService> logger)
    {
        _finnhubService = finnhubService;
        _polygonService = polygonService;
        _newsApiService = newsApiService;
        _fmpService = fmpService;
        _quoteStream = quoteStream;
        _providerHealth = providerHealth;
        _apiUsageTracker = apiUsageTracker;
        _cache = cache;
        _logger = logger;
    }

    public async Task<UnifiedMarketData> GetUnifiedMarketDataAsync(
        string symbol,
        bool includeNews,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var marketCacheKey = $"market:{normalizedSymbol}:{includeNews}";

        if (!ShouldBypassUnifiedCache(normalizedSymbol, DateTimeOffset.UtcNow) &&
            _cache.TryGetValue<UnifiedMarketData>(marketCacheKey, out var cachedUnified) &&
            cachedUnified is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return CloneWithQuality(cachedUnified, isCached: true, isFallback: cachedUnified.IsFallback);
        }
        Interlocked.Increment(ref _cacheMisses);

        var quote = await GetOrLoadQuoteAsync(normalizedSymbol, cancellationToken);
        if (!IsQuoteUsable(quote))
        {
            if (_lastKnownBySymbol.TryGetValue(normalizedSymbol, out var fallbackFromMemory))
            {
                Interlocked.Increment(ref _fallbackUsed);
                Interlocked.Increment(ref _staleDataReturns);
                return CloneWithQuality(fallbackFromMemory, isCached: true, isFallback: true);
            }

            var minimal = BuildUnified(
                normalizedSymbol,
                CreateMinimalQuote(),
                null,
                null,
                sourceProvider: "CACHE",
                isCached: true,
                isFallback: true);
            _lastKnownBySymbol[normalizedSymbol] = minimal;
            _cache.Set(marketCacheKey, minimal, TimeSpan.FromSeconds(5));
            Interlocked.Increment(ref _fallbackUsed);
            Interlocked.Increment(ref _staleDataReturns);
            return CloneWithQuality(minimal, isCached: true, isFallback: true);
        }

        var fundamentals = await GetOrLoadFundamentalsAsync(normalizedSymbol, cancellationToken);
        var news = includeNews ? await GetOrLoadGlobalNewsForSymbolAsync(normalizedSymbol, cancellationToken) : null;

        var sourceProvider = quote!.Provider switch
        {
            nameof(FinnhubService) => "FINNHUB",
            nameof(PolygonService) => "POLYGON",
            "WebSocket" => "WebSocket",
            _ => "CACHE"
        };

        var unified = BuildUnified(
            normalizedSymbol,
            quote,
            fundamentals,
            news,
            sourceProvider,
            isCached: false,
            isFallback: sourceProvider is "POLYGON" or "CACHE");

        _lastKnownBySymbol[normalizedSymbol] = unified;
        _cache.Set(marketCacheKey, unified, PriceTtl);
        return CloneWithQuality(unified, isCached: false, isFallback: unified.IsFallback);
    }

    public async Task<UnifiedMarketData?> GetUnifiedMarketData(string symbol, CancellationToken cancellationToken = default)
    {
        return await GetUnifiedMarketDataAsync(symbol, includeNews: true, cancellationToken);
    }

    public async Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var quote = await GetOrLoadQuoteAsync(normalizedSymbol, cancellationToken);
        if (IsQuoteUsable(quote))
        {
            Interlocked.Increment(ref _totalQuoteResults);
            return quote;
        }

        if (_lastKnownBySymbol.TryGetValue(normalizedSymbol, out var fallback))
        {
            Interlocked.Increment(ref _fallbackUsed);
            Interlocked.Increment(ref _staleDataReturns);
            Interlocked.Increment(ref _totalQuoteResults);
            return fallback.Quote;
        }

        return null;
    }

    public void MarkTopOpportunity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _forceFreshSymbols[symbol.Trim().ToUpperInvariant()] = DateTimeOffset.UtcNow;
        _ = _quoteStream.SubscribeAsync(symbol);
    }

    public MarketDataHealthMetrics GetHealthMetrics()
    {
        var staleReturns = Interlocked.Read(ref _staleDataReturns);
        var totalQuoteResults = Math.Max(1, Interlocked.Read(ref _totalQuoteResults));
        var staleRatePercent = Math.Round((staleReturns * 100d) / totalQuoteResults, 2);
        var (p50, p95, p99) = GetProviderLatencyPercentiles();

        return new MarketDataHealthMetrics
        {
            TotalApiCalls = Interlocked.Read(ref _totalApiCalls),
            SuccessfulCalls = Interlocked.Read(ref _successfulCalls),
            FailedCalls = Interlocked.Read(ref _failedCalls),
            FallbackUsed = Interlocked.Read(ref _fallbackUsed),
            Errors = Interlocked.Read(ref _errors),
            ProviderSuccessCount = Interlocked.Read(ref _successfulCalls),
            ProviderFailureCount = Interlocked.Read(ref _failedCalls),
            RateLimitHits = _apiUsageTracker.GetUsageSnapshot().Sum(x => x.RateLimitHits),
            WebsocketReconnectCount = _quoteStream.ReconnectCount,
            CacheHitRatio = ComputeCacheHitRatio(),
            StaleDataReturnCount = staleReturns,
            StaleDataRatePercent = staleRatePercent,
            ProviderLatencyP50Ms = p50,
            ProviderLatencyP95Ms = p95,
            ProviderLatencyP99Ms = p99,
            ProviderCount = 4,
            Providers = _providerHealth.GetSnapshot()
        };
    }

    public Task PersistProviderPerformanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private async Task<QuoteResponse?> GetOrLoadQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        _ = _quoteStream.SubscribeAsync(symbol);
        var cacheKey = $"price:{symbol}:free-tier";

        if (_cache.TryGetValue<QuoteResponse>(cacheKey, out var cachedQuote) && IsQuoteUsable(cachedQuote))
        {
            Interlocked.Increment(ref _cacheHits);
            RefreshQuoteFreshness(cachedQuote!);
            if (!cachedQuote!.IsStale)
            {
                Interlocked.Increment(ref _totalQuoteResults);
                return cachedQuote;
            }
        }
        else
        {
            Interlocked.Increment(ref _cacheMisses);
        }

        var quote = await GetOrRunDedupAsync(
            _inflightQuotes,
            cacheKey,
            async () => await LoadQuoteStreamFirstAsync(symbol, cancellationToken));

        if (!IsQuoteUsable(quote))
        {
            return null;
        }

        RefreshQuoteFreshness(quote!);
        _cache.Set(cacheKey, quote!, PriceTtl);
        Interlocked.Increment(ref _totalQuoteResults);
        return quote;
    }

    private async Task<QuoteResponse?> LoadQuoteStreamFirstAsync(string symbol, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (_quoteStream.TryGetFreshPrice(symbol, out var streamedPrice))
        {
            var age = now - streamedPrice.ReceivedTimestampUtc;
            if (age <= QuoteStaleAfter)
            {
                var quoteFromStream = BuildQuoteFromStream(symbol, streamedPrice);
                _providerHealth.RecordSuccess("WebSocket", 0);
                Interlocked.Increment(ref _totalQuoteResults);
                return quoteFromStream;
            }
        }

        var finnhubQuote = await SafeProviderQuoteCallAsync(
            providerName: nameof(FinnhubService),
            failureThreshold: 3,
            cooldown: TimeSpan.FromSeconds(60),
            baseBackoff: TimeSpan.FromSeconds(2),
            maxBackoff: TimeSpan.FromSeconds(20),
            call: ct => _finnhubService.GetQuoteAsync(symbol, ct),
            cancellationToken: cancellationToken);

        if (IsQuoteUsable(finnhubQuote))
        {
            ApplyQuoteTimeDefaults(finnhubQuote!);
            finnhubQuote!.Provider = nameof(FinnhubService);
            RefreshQuoteFreshness(finnhubQuote);
            Interlocked.Increment(ref _totalQuoteResults);
            return finnhubQuote;
        }

        QuoteResponse? polygonQuote = null;
        if (TryConsumePolygonBudget())
        {
            polygonQuote = await SafeProviderQuoteCallAsync(
                providerName: nameof(PolygonService),
                failureThreshold: 2,
                cooldown: TimeSpan.FromMinutes(3),
                baseBackoff: TimeSpan.FromSeconds(5),
                maxBackoff: TimeSpan.FromSeconds(45),
                call: ct => _polygonService.GetQuoteAsync(symbol, ct),
                cancellationToken: cancellationToken);
        }

        if (IsQuoteUsable(polygonQuote))
        {
            ApplyQuoteTimeDefaults(polygonQuote!);
            polygonQuote!.Provider = nameof(PolygonService);
            RefreshQuoteFreshness(polygonQuote);
            Interlocked.Increment(ref _fallbackUsed);
            Interlocked.Increment(ref _totalQuoteResults);
            return polygonQuote;
        }

        if (_lastKnownBySymbol.TryGetValue(symbol, out var fallback))
        {
            Interlocked.Increment(ref _fallbackUsed);
            Interlocked.Increment(ref _staleDataReturns);
            var copy = CloneQuote(fallback.Quote);
            RefreshQuoteFreshness(copy);
            copy.Provider = "CACHE";
            copy.IsStale = true;
            _logger.LogWarning(
                "[API] FALLBACK_CACHE -> {Symbol} age={AgeSeconds}s",
                symbol,
                copy.AgeSeconds);
            Interlocked.Increment(ref _totalQuoteResults);
            return copy;
        }

        _logger.LogWarning("[API] ALL_FAILED -> {Symbol}", symbol);
        return null;
    }

    private async Task<FmpFactors?> GetOrLoadFundamentalsAsync(string symbol, CancellationToken cancellationToken)
    {
        var cacheKey = $"fundamentals:{symbol}:free-tier";
        if (_cache.TryGetValue<FmpFactors>(cacheKey, out var cached) && cached is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);

        var factors = await GetOrRunDedupAsync(
            _inflightFundamentals,
            cacheKey,
            async () => await SafeCallAsync(
                providerName: nameof(FmpService),
                failureThreshold: 2,
                cooldown: TimeSpan.FromMinutes(10),
                baseBackoff: TimeSpan.FromSeconds(8),
                maxBackoff: TimeSpan.FromMinutes(2),
                call: ct => _fmpService.GetFactorsAsync(symbol, ct),
                cancellationToken));

        if (factors is not null)
        {
            _cache.Set(cacheKey, factors, FundamentalsTtl);
        }

        return factors;
    }

    private async Task<NormalizedNewsItem?> GetOrLoadGlobalNewsForSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        var globalArticles = await GetOrLoadGlobalNewsAsync(cancellationToken);
        if (globalArticles.Count == 0)
        {
            return null;
        }

        var matched = globalArticles.FirstOrDefault(article => ArticleMatchesSymbol(article, symbol))
            ?? globalArticles.FirstOrDefault();

        if (matched is null)
        {
            return null;
        }

        var headline = matched.Title?.Trim() ?? string.Empty;
        var summary = matched.Description?.Trim() ?? string.Empty;
        var sentimentScore = ComputeSentimentScore(headline, summary);

        return new NormalizedNewsItem
        {
            Symbol = symbol,
            Headline = headline,
            Summary = summary,
            Source = string.IsNullOrWhiteSpace(matched.Source?.Name) ? "NEWSAPI" : matched.Source!.Name!.Trim(),
            Url = matched.Url?.Trim() ?? string.Empty,
            Datetime = matched.PublishedAt ?? DateTimeOffset.UtcNow,
            SentimentScore = sentimentScore,
            Sentiment = ResolveSentiment(sentimentScore),
            Category = "market commentary"
        };
    }

    private async Task<IReadOnlyList<NewsApiArticle>> GetOrLoadGlobalNewsAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "news:global:free-tier";
        if (_cache.TryGetValue<IReadOnlyList<NewsApiArticle>>(cacheKey, out var cached) && cached is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);

        async Task<IReadOnlyList<NewsApiArticle>?> LoadGlobalNewsAsync(CancellationToken ct)
        {
            var result = await _newsApiService.GetGlobalMarketArticlesAsync(20, ct);
            return result;
        }

        var loaded = await GetOrRunDedupAsync(
            _inflightGlobalNews,
            cacheKey,
            async () => await SafeCallAsync(
                providerName: nameof(ExternalNewsApiService),
                failureThreshold: 2,
                cooldown: TimeSpan.FromMinutes(3),
                baseBackoff: TimeSpan.FromSeconds(10),
                maxBackoff: TimeSpan.FromMinutes(3),
                call: LoadGlobalNewsAsync,
                cancellationToken)
                ?? []);

        var normalized = (loaded ?? [])
            .Where(article => !string.IsNullOrWhiteSpace(article.Title) && !string.IsNullOrWhiteSpace(article.Url))
            .OrderByDescending(article => article.PublishedAt)
            .Take(20)
            .ToList();

        _cache.Set(cacheKey, normalized, GlobalNewsTtl);
        return normalized;
    }

    private async Task<T?> GetOrRunDedupAsync<T>(
        ConcurrentDictionary<string, Lazy<Task<T?>>> inflight,
        string key,
        Func<Task<T?>> factory) where T : class
    {
        var lazy = inflight.GetOrAdd(key, _ => new Lazy<Task<T?>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            inflight.TryRemove(key, out _);
        }
    }

    private async Task<QuoteResponse?> SafeProviderQuoteCallAsync(
        string providerName,
        int failureThreshold,
        TimeSpan cooldown,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff,
        Func<CancellationToken, Task<QuoteResponse?>> call,
        CancellationToken cancellationToken)
    {
        var quote = await SafeCallAsync(
            providerName,
            failureThreshold,
            cooldown,
            baseBackoff,
            maxBackoff,
            call,
            cancellationToken);

        if (IsQuoteUsable(quote))
        {
            return quote;
        }

        return null;
    }

    private async Task<T?> SafeCallAsync<T>(
        string providerName,
        int failureThreshold,
        TimeSpan cooldown,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff,
        Func<CancellationToken, Task<T?>> call,
        CancellationToken cancellationToken) where T : class
    {
        if (!_providerHealth.CanExecute(providerName, out var wait))
        {
            _logger.LogWarning("[API] CIRCUIT_OPEN -> {Provider} for {WaitSeconds}s", providerName, Math.Ceiling(wait.TotalSeconds));
            return null;
        }

        await GlobalApiGate.WaitAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _totalApiCalls);
        var rateLimitBefore = _apiUsageTracker.GetRateLimitHits(providerName);

        try
        {
            var result = await call(cancellationToken);
            var rateLimitAfter = _apiUsageTracker.GetRateLimitHits(providerName);
            var rateLimited = rateLimitAfter > rateLimitBefore;

            if (result is null)
            {
                Interlocked.Increment(ref _failedCalls);
                _providerHealth.RecordFailure(
                    providerName,
                    sw.ElapsedMilliseconds,
                    rateLimited,
                    failureThreshold,
                    cooldown,
                    baseBackoff,
                    maxBackoff);
                return null;
            }

            Interlocked.Increment(ref _successfulCalls);
            _providerHealth.RecordSuccess(providerName, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Interlocked.Increment(ref _failedCalls);
            _providerHealth.RecordFailure(
                providerName,
                sw.ElapsedMilliseconds,
                isRateLimited: false,
                failureThreshold,
                cooldown,
                baseBackoff,
                maxBackoff);
            _logger.LogWarning(ex, "[API] EXCEPTION -> {Provider}", providerName);
            return null;
        }
        finally
        {
            sw.Stop();
            TrackProviderLatency(sw.ElapsedMilliseconds);
            GlobalApiGate.Release();
        }
    }

    private void TrackProviderLatency(long elapsedMs)
    {
        lock (_latencyGate)
        {
            _providerLatencyMsSamples.Enqueue(Math.Max(0, elapsedMs));
            while (_providerLatencyMsSamples.Count > MaxLatencySamples)
            {
                _providerLatencyMsSamples.Dequeue();
            }
        }
    }

    private (double p50, double p95, double p99) GetProviderLatencyPercentiles()
    {
        long[] snapshot;
        lock (_latencyGate)
        {
            snapshot = _providerLatencyMsSamples.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return (0d, 0d, 0d);
        }

        Array.Sort(snapshot);
        return (
            Percentile(snapshot, 0.50),
            Percentile(snapshot, 0.95),
            Percentile(snapshot, 0.99));
    }

    private static double Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0d;
        }

        var index = (int)Math.Floor(percentile * (sorted.Length - 1));
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return Math.Round((double)sorted[index], 2);
    }

    private UnifiedMarketData BuildUnified(
        string symbol,
        QuoteResponse quote,
        FmpFactors? factors,
        NormalizedNewsItem? news,
        string sourceProvider,
        bool isCached,
        bool isFallback)
    {
        RefreshQuoteFreshness(quote);
        var previousClose = quote.PreviousClose > 0m ? quote.PreviousClose : quote.CurrentPrice;
        var changePercent = previousClose > 0m
            ? Math.Round(((quote.CurrentPrice - previousClose) / previousClose) * 100m, 2)
            : 0m;

        return new UnifiedMarketData
        {
            Symbol = symbol,
            Price = Math.Round(Math.Max(quote.CurrentPrice, 0.01m), 2),
            ChangePercent = changePercent,
            Volume = Math.Round(Math.Max(quote.Volume, 0m), 0),
            News = news,
            Sentiment = news?.Sentiment ?? (changePercent >= 0m ? "BULLISH" : "BEARISH"),
            MarketCap = factors?.MarketCap,
            FloatShares = factors?.FloatShares,
            InstitutionalOwnership = factors?.InstitutionalOwnership,
            Quote = quote,
            PriceSource = sourceProvider,
            VolumeSource = sourceProvider,
            SourceProvider = sourceProvider,
            IsCached = isCached,
            IsFallback = isFallback,
            DataAgeSeconds = quote.AgeSeconds,
            IsStale = isFallback || quote.IsStale
        };
    }

    private static void RefreshQuoteFreshness(QuoteResponse quote)
    {
        var ageSeconds = ComputeAgeSeconds(quote);
        quote.AgeSeconds = ageSeconds;
        quote.IsStale = ageSeconds > (int)QuoteStaleAfter.TotalSeconds;
    }

    private static int ComputeAgeSeconds(QuoteResponse quote)
    {
        if (quote.ReceivedAtUtc is not null)
        {
            var age = DateTimeOffset.UtcNow - quote.ReceivedAtUtc.Value;
            return age <= TimeSpan.Zero ? 0 : (int)Math.Floor(age.TotalSeconds);
        }

        if (quote.Timestamp <= 0)
        {
            return int.MaxValue;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (int)Math.Max(0, now - quote.Timestamp);
    }

    private QuoteResponse BuildQuoteFromStream(string symbol, StreamPriceSnapshot streamedPrice)
    {
        decimal previousClose = streamedPrice.Price;
        if (_lastKnownBySymbol.TryGetValue(symbol, out var previous) && previous.Quote.PreviousClose > 0m)
        {
            previousClose = previous.Quote.PreviousClose;
        }

        var quote = new QuoteResponse
        {
            CurrentPrice = Math.Round(streamedPrice.Price, 2),
            PreviousClose = Math.Round(previousClose, 2),
            OpenPrice = Math.Round(previousClose, 2),
            High = Math.Round(Math.Max(streamedPrice.Price, previousClose), 2),
            Low = Math.Round(Math.Min(streamedPrice.Price, previousClose), 2),
            Volume = Math.Round(Math.Max(streamedPrice.Volume, 0m), 0),
            Timestamp = streamedPrice.TradeTimestampUtc.ToUnixTimeSeconds(),
            TradeTimeUtc = streamedPrice.TradeTimestampUtc,
            ReceivedAtUtc = streamedPrice.ReceivedTimestampUtc,
            Provider = "WebSocket"
        };

        RefreshQuoteFreshness(quote);
        return quote;
    }

    private static bool IsQuoteUsable(QuoteResponse? quote)
    {
        return quote is not null && quote.CurrentPrice > 0m && quote.PreviousClose > 0m;
    }

    private static QuoteResponse CreateMinimalQuote()
    {
        return new QuoteResponse
        {
            CurrentPrice = 1m,
            PreviousClose = 1m,
            OpenPrice = 1m,
            High = 1m,
            Low = 1m,
            Volume = 0m,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TradeTimeUtc = null,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            Provider = "CACHE",
            AgeSeconds = int.MaxValue,
            IsStale = true
        };
    }

    private static string NormalizeSymbol(string symbol)
    {
        return string.IsNullOrWhiteSpace(symbol) ? "SPY" : symbol.Trim().ToUpperInvariant();
    }

    private bool ShouldBypassUnifiedCache(string symbol, DateTimeOffset now)
    {
        if (_forceFreshSymbols.TryGetValue(symbol, out var markedAt))
        {
            if (now - markedAt <= TopOpportunityBypassWindow)
            {
                return true;
            }

            _forceFreshSymbols.TryRemove(symbol, out _);
        }

        return false;
    }

    private static bool TryConsumePolygonBudget()
    {
        lock (PolygonBudgetGate)
        {
            var now = DateTimeOffset.UtcNow;
            while (PolygonBudgetWindow.Count > 0 && now - PolygonBudgetWindow.Peek() > TimeSpan.FromMinutes(1))
            {
                PolygonBudgetWindow.Dequeue();
            }

            if (PolygonBudgetWindow.Count >= MaxPolygonCallsPerMinute)
            {
                return false;
            }

            PolygonBudgetWindow.Enqueue(now);
            return true;
        }
    }

    private static bool ArticleMatchesSymbol(NewsApiArticle article, string symbol)
    {
        var needle = symbol.ToUpperInvariant();
        var title = article.Title?.ToUpperInvariant() ?? string.Empty;
        var summary = article.Description?.ToUpperInvariant() ?? string.Empty;
        return title.Contains(needle, StringComparison.Ordinal) || summary.Contains(needle, StringComparison.Ordinal);
    }

    private static decimal ComputeSentimentScore(string headline, string summary)
    {
        var text = $"{headline} {summary}".ToLowerInvariant();
        var positive = 0;
        var negative = 0;

        if (text.Contains("beat") || text.Contains("surge") || text.Contains("upgrade") || text.Contains("growth"))
        {
            positive++;
        }

        if (text.Contains("miss") || text.Contains("downgrade") || text.Contains("lawsuit") || text.Contains("decline"))
        {
            negative++;
        }

        return Math.Clamp((positive - negative) / 2m, -1m, 1m);
    }

    private static string ResolveSentiment(decimal score)
    {
        if (score > 0.2m)
        {
            return "BULLISH";
        }

        if (score < -0.2m)
        {
            return "BEARISH";
        }

        return "NEUTRAL";
    }

    private static QuoteResponse CloneQuote(QuoteResponse quote)
    {
        return new QuoteResponse
        {
            CurrentPrice = quote.CurrentPrice,
            PreviousClose = quote.PreviousClose,
            High = quote.High,
            Low = quote.Low,
            Volume = quote.Volume,
            OpenPrice = quote.OpenPrice,
            Timestamp = quote.Timestamp,
            TradeTimeUtc = quote.TradeTimeUtc,
            ReceivedAtUtc = quote.ReceivedAtUtc,
            Provider = quote.Provider,
            AgeSeconds = quote.AgeSeconds,
            IsStale = quote.IsStale
        };
    }

    private static void ApplyQuoteTimeDefaults(QuoteResponse quote)
    {
        if (quote.TradeTimeUtc is null && quote.Timestamp > 0)
        {
            quote.TradeTimeUtc = DateTimeOffset.FromUnixTimeSeconds(quote.Timestamp);
        }

        quote.ReceivedAtUtc ??= DateTimeOffset.UtcNow;
    }

    private static UnifiedMarketData CloneWithQuality(UnifiedMarketData input, bool isCached, bool isFallback)
    {
        var quote = CloneQuote(input.Quote);
        RefreshQuoteFreshness(quote);
        if (isFallback)
        {
            quote.IsStale = true;
        }

        return new UnifiedMarketData
        {
            Symbol = input.Symbol,
            Price = input.Price,
            ChangePercent = input.ChangePercent,
            Volume = input.Volume,
            News = input.News,
            Sentiment = input.Sentiment,
            MarketCap = input.MarketCap,
            FloatShares = input.FloatShares,
            InstitutionalOwnership = input.InstitutionalOwnership,
            Quote = quote,
            PriceSource = input.PriceSource,
            VolumeSource = input.VolumeSource,
            SourceProvider = input.SourceProvider,
            IsCached = isCached,
            IsFallback = isFallback,
            DataAgeSeconds = quote.AgeSeconds,
            IsStale = isFallback || quote.IsStale
        };
    }

    private double ComputeCacheHitRatio()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = hits + misses;
        if (total == 0)
        {
            return 1d;
        }

        return Math.Round((double)hits / total, 4);
    }
}
