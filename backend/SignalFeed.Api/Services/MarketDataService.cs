using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class MarketDataService : IMarketDataService
{
    private sealed record PriceCacheEntry(
        decimal CurrentPrice,
        decimal ChangePercent,
        decimal PreviousClose,
        decimal OpenPrice,
        decimal HighPrice,
        decimal LowPrice,
        decimal Volume,
        string PriceSource,
        string VolumeSource);

    private sealed record CachedNewsValue(NormalizedNewsItem? Value);
    private sealed record CachedFundamentalsValue(FmpFactors? Value);

    private static readonly TimeSpan PriceTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NewsTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FundamentalsTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TopOpportunityBypassWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProviderSkipWindow = TimeSpan.FromSeconds(60);
    private const int ProviderFailureThreshold = 5;
    private const string ProviderStatsFileName = "provider-stats.json";

    private readonly PolygonService _polygonService;
    private readonly NewsAggregationService _newsAggregationService;
    private readonly FmpService _fmpService;
    private readonly List<IQuoteProvider> _providers;
    private readonly ApiUsageTracker _apiUsageTracker;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MarketDataService> _logger;
    private readonly ConcurrentDictionary<string, UnifiedMarketData> _lastKnownBySymbol = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forceFreshSymbols = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _providerFailureCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _disabledProviders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _smoothedScores = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderStats> _providerStats = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _providerStatsFileGate = new(1, 1);
    private readonly string _providerStatsFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private long _totalApiCalls;
    private long _successfulCalls;
    private long _failedCalls;
    private long _fallbackUsed;
    private long _errors;

    public MarketDataService(
        PolygonService polygonService,
        NewsAggregationService newsAggregationService,
        FmpService fmpService,
        IEnumerable<IQuoteProvider> providers,
        ApiUsageTracker apiUsageTracker,
        IHostEnvironment hostEnvironment,
        IMemoryCache cache,
        ILogger<MarketDataService> logger)
    {
        _polygonService = polygonService;
        _newsAggregationService = newsAggregationService;
        _fmpService = fmpService;
        _providers = providers
            .Where(provider => provider is not null)
            .DistinctBy(provider => provider.GetType().FullName)
            .ToList();
        _apiUsageTracker = apiUsageTracker;
        _cache = cache;
        _logger = logger;
        _providerStatsFilePath = Path.Combine(hostEnvironment.ContentRootPath, ProviderStatsFileName);

        foreach (var provider in _providers)
        {
            var providerName = provider.GetType().Name;
            var stats = _providerStats.GetOrAdd(providerName, _ => new ProviderStats { Provider = providerName });
            _smoothedScores.TryAdd(providerName, (double)ComputeProviderScore(stats));
        }

        TryLoadProviderPerformanceState();
    }

    public async Task<UnifiedMarketData> GetUnifiedMarketDataAsync(
        string symbol,
        bool includeNews,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var marketCacheKey = $"market:{normalizedSymbol}";
        if (!ShouldBypassUnifiedCache(normalizedSymbol, DateTimeOffset.UtcNow) &&
            _cache.TryGetValue<UnifiedMarketData>(marketCacheKey, out var cachedUnified) &&
            cachedUnified is not null)
        {
            _logger.LogInformation("CACHE HIT {symbol}", normalizedSymbol);
            return cachedUnified;
        }

        _logger.LogInformation("CACHE MISS {symbol}", normalizedSymbol);
        var priceTask = GetOrLoadPriceAsync(normalizedSymbol, cancellationToken);
        var factorsTask = GetOrLoadFundamentalsAsync(normalizedSymbol, cancellationToken);
        Task<NormalizedNewsItem?>? newsTask = includeNews
            ? GetOrLoadNewsAsync(normalizedSymbol, cancellationToken)
            : null;

        var tasks = new List<Task> { priceTask, factorsTask };
        if (newsTask is not null)
        {
            tasks.Add(newsTask);
        }

        await Task.WhenAll(tasks);

        var price = priceTask.Result;
        var factors = factorsTask.Result;
        var news = newsTask?.Result;

        var sentiment = ResolveSentiment(news, price.ChangePercent);
        var result = BuildAndCache(
            normalizedSymbol,
            price.CurrentPrice,
            price.ChangePercent,
            price.PreviousClose,
            price.OpenPrice,
            price.HighPrice,
            price.LowPrice,
            price.Volume,
            news,
            sentiment,
            factors?.MarketCap,
            factors?.FloatShares,
            factors?.InstitutionalOwnership,
            price.PriceSource,
            price.VolumeSource);

        if (result is not null)
        {
            var ttlSeconds = ResolveUnifiedTtlSeconds(result);
            _cache.Set(marketCacheKey, result, TimeSpan.FromSeconds(ttlSeconds));
        }

        return result!;
    }

    public async Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (_providers.Count == 0)
        {
            _logger.LogError("[API] ALL_FAILED -> {symbol} (no providers registered)", normalizedSymbol);
            Interlocked.Increment(ref _failedCalls);
            Interlocked.Increment(ref _errors);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var rankedProviders = GetRankedProviders(now);
        var rankingSummary = string.Join(", ", rankedProviders.Select(entry =>
            $"{entry.Provider.GetType().Name}:{entry.Score:F2}"));
        _logger.LogInformation("[API] RANKING -> {Ranking}", rankingSummary);

        for (var attempt = 0; attempt < rankedProviders.Count; attempt++)
        {
            var provider = rankedProviders[attempt].Provider;
            var providerName = provider.GetType().Name;

            Interlocked.Increment(ref _totalApiCalls);
            _logger.LogInformation("[API] TRY -> {Provider}", providerName);

            QuoteResponse? quote = null;
            var rateLimitHitsBeforeCall = _apiUsageTracker.GetRateLimitHits(providerName);
            var sw = Stopwatch.StartNew();

            try
            {
                quote = await provider.GetQuoteAsync(normalizedSymbol, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[API] FAIL -> {Provider}", providerName);
            }

            sw.Stop();
            var rateLimitHitsAfterCall = _apiUsageTracker.GetRateLimitHits(providerName);
            var rateLimitDelta = Math.Max(0, rateLimitHitsAfterCall - rateLimitHitsBeforeCall);

            if (quote is not null && quote.CurrentPrice > 0m && quote.PreviousClose > 0m)
            {
                quote.Provider = providerName;
                _providerFailureCounts[providerName] = 0;
                Interlocked.Increment(ref _successfulCalls);
                if (attempt > 0)
                {
                    Interlocked.Increment(ref _fallbackUsed);
                }

                UpdateProviderStats(providerName, success: true, sw.ElapsedMilliseconds, rateLimitDelta);
                _logger.LogInformation("[API] SUCCESS -> {Provider} -> {LatencyMs}ms", providerName, sw.ElapsedMilliseconds);
                return quote;
            }

            Interlocked.Increment(ref _failedCalls);
            Interlocked.Increment(ref _errors);
            UpdateProviderStats(providerName, success: false, sw.ElapsedMilliseconds, rateLimitDelta);
            var failures = _providerFailureCounts.AddOrUpdate(providerName, 1, (_, count) => count + 1);
            _logger.LogWarning("[API] FAIL -> {Provider}", providerName);

            if (failures >= ProviderFailureThreshold)
            {
                var disabledUntil = DateTime.UtcNow.Add(ProviderSkipWindow);
                var isAlreadyDisabled = _disabledProviders.TryGetValue(providerName, out var existingUntil) &&
                    existingUntil > DateTime.UtcNow;
                _disabledProviders[providerName] = disabledUntil;
                if (!isAlreadyDisabled)
                {
                    _logger.LogWarning("[API] DISABLED -> {Provider}", providerName);
                }
            }

            if (attempt < rankedProviders.Count - 1)
            {
                var nextProvider = rankedProviders[attempt + 1].Provider.GetType().Name;
                _logger.LogInformation("[API] FALLBACK -> {NextProvider}", nextProvider);
            }
        }

        _logger.LogError("[API] ALL_FAILED -> {symbol}", normalizedSymbol);
        return null;
    }

    private async Task<PriceCacheEntry> GetOrLoadPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        var cacheKey = $"price:{symbol}";
        if (_cache.TryGetValue<PriceCacheEntry>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogInformation("Cache HIT {key}", cacheKey);
            return cached;
        }

        _logger.LogInformation("Cache MISS {key}", cacheKey);
        var value = await FetchPriceAsync(symbol, cancellationToken);
        _cache.Set(cacheKey, value, PriceTtl);
        return value;
    }

    private async Task<NormalizedNewsItem?> GetOrLoadNewsAsync(string symbol, CancellationToken cancellationToken)
    {
        var cacheKey = $"news:{symbol}";
        if (_cache.TryGetValue<CachedNewsValue>(cacheKey, out var cached))
        {
            _logger.LogInformation("Cache HIT {key}", cacheKey);
            return cached?.Value;
        }

        _logger.LogInformation("Cache MISS {key}", cacheKey);
        var news = await _newsAggregationService.GetLatestNewsAsync(symbol, cancellationToken);
        _cache.Set(cacheKey, new CachedNewsValue(news), NewsTtl);
        return news;
    }

    private async Task<FmpFactors?> GetOrLoadFundamentalsAsync(string symbol, CancellationToken cancellationToken)
    {
        var cacheKey = $"fundamentals:{symbol}";
        if (_cache.TryGetValue<CachedFundamentalsValue>(cacheKey, out var cached))
        {
            _logger.LogInformation("Cache HIT {key}", cacheKey);
            return cached?.Value;
        }

        _logger.LogInformation("Cache MISS {key}", cacheKey);
        var factors = await _fmpService.GetFactorsAsync(symbol, cancellationToken);
        _cache.Set(cacheKey, new CachedFundamentalsValue(factors), FundamentalsTtl);
        return factors;
    }

    private async Task<PriceCacheEntry> FetchPriceAsync(string normalizedSymbol, CancellationToken cancellationToken)
    {
        var polygonSnapshotTask = _polygonService.GetSnapshotAsync(normalizedSymbol, cancellationToken);
        var polygonAggregateTask = _polygonService.GetPreviousAggregateAsync(normalizedSymbol, cancellationToken);
        await Task.WhenAll(polygonSnapshotTask, polygonAggregateTask);

        var polygonSnapshot = polygonSnapshotTask.Result;
        var polygonAggregate = polygonAggregateTask.Result;

        var polygonPrice = polygonSnapshot?.LastTrade?.Price ?? 0m;
        if (polygonPrice <= 0m)
        {
            polygonPrice = polygonSnapshot?.Day?.Close ?? 0m;
        }

        var polygonChangePercent = polygonSnapshot?.TodaysChangePercent;
        var polygonVolume = polygonSnapshot?.Day?.Volume ?? 0m;

        QuoteResponse? fallbackQuote = null;
        decimal finnhubPrice = 0m;
        decimal finnhubPreviousClose = 0m;

        var usePolygonPrice = polygonPrice > 0m && polygonChangePercent is not null;
        var usePolygonVolume = polygonVolume > 0m;

        decimal currentPrice;
        decimal previousClose;
        decimal changePercent;
        decimal openPrice;
        decimal highPrice;
        decimal lowPrice;
        decimal volume;
        var priceSource = "MINIMAL";
        var volumeSource = "MINIMAL";

        if (usePolygonPrice)
        {
            currentPrice = polygonPrice;
            changePercent = Math.Round(polygonChangePercent!.Value, 2);
            previousClose = currentPrice / (1m + (changePercent / 100m));
            openPrice = polygonSnapshot?.Day?.Open ?? 0m;
            highPrice = polygonSnapshot?.Day?.High ?? 0m;
            lowPrice = polygonSnapshot?.Day?.Low ?? 0m;
            priceSource = "POLYGON";
            _logger.LogInformation("Polygon used for {Symbol}.", normalizedSymbol);
        }
        else
        {
            fallbackQuote = await GetQuoteAsync(normalizedSymbol, cancellationToken);
            finnhubPrice = fallbackQuote?.CurrentPrice ?? 0m;
            finnhubPreviousClose = fallbackQuote?.PreviousClose ?? 0m;

            if (finnhubPrice > 0m && finnhubPreviousClose > 0m)
            {
                Interlocked.Increment(ref _fallbackUsed);
                currentPrice = finnhubPrice;
                previousClose = finnhubPreviousClose;
                changePercent = Math.Round(((currentPrice - previousClose) / previousClose) * 100m, 2);
                openPrice = fallbackQuote?.OpenPrice > 0m ? fallbackQuote.OpenPrice : previousClose;
                highPrice = fallbackQuote?.High > 0m ? fallbackQuote.High : currentPrice;
                lowPrice = fallbackQuote?.Low > 0m ? fallbackQuote.Low : currentPrice;
                priceSource = "FALLBACK";
            }
            else if (_lastKnownBySymbol.TryGetValue(normalizedSymbol, out var cached))
            {
                currentPrice = cached.Price > 0 ? cached.Price : 1m;
                changePercent = cached.ChangePercent;
                previousClose = currentPrice / (1m + (changePercent / 100m));
                openPrice = cached.Quote.OpenPrice > 0 ? cached.Quote.OpenPrice : previousClose;
                highPrice = cached.Quote.High > 0 ? cached.Quote.High : currentPrice;
                lowPrice = cached.Quote.Low > 0 ? cached.Quote.Low : currentPrice;
                volume = cached.Volume;
                return new PriceCacheEntry(
                    CurrentPrice: currentPrice,
                    ChangePercent: changePercent,
                    PreviousClose: previousClose,
                    OpenPrice: openPrice,
                    HighPrice: highPrice,
                    LowPrice: lowPrice,
                    Volume: volume,
                    PriceSource: "CACHED_FALLBACK",
                    VolumeSource: cached.VolumeSource);
            }
            else
            {
                currentPrice = 1m;
                changePercent = 0m;
                previousClose = 1m;
                openPrice = 1m;
                highPrice = 1m;
                lowPrice = 1m;
                _logger.LogWarning("All APIs failed for {Symbol}. Emitting minimal fallback market data.", normalizedSymbol);
            }
        }

        if (usePolygonVolume)
        {
            volume = polygonVolume;
            volumeSource = "POLYGON";
        }
        else
        {
            volume = fallbackQuote?.Volume ?? polygonAggregate?.Volume ?? 0m;
            volumeSource = volume > 0 ? "FINNHUB" : "MINIMAL";
            if (volume > 0)
            {
                _logger.LogInformation("Finnhub fallback used for volume on {Symbol}.", normalizedSymbol);
            }
        }

        // Use aggregate data to improve OHLC completeness when available.
        if (openPrice <= 0m)
        {
            openPrice = polygonAggregate?.Open > 0m ? polygonAggregate.Open : previousClose;
        }

        if (highPrice <= 0m)
        {
            highPrice = polygonAggregate?.High > 0m ? polygonAggregate.High : currentPrice;
        }

        if (lowPrice <= 0m)
        {
            lowPrice = polygonAggregate?.Low > 0m ? polygonAggregate.Low : currentPrice;
        }

        return new PriceCacheEntry(
            CurrentPrice: currentPrice,
            ChangePercent: changePercent,
            PreviousClose: previousClose,
            OpenPrice: openPrice,
            HighPrice: highPrice,
            LowPrice: lowPrice,
            Volume: volume,
            PriceSource: priceSource,
            VolumeSource: volumeSource);
    }

    private UnifiedMarketData BuildAndCache(
        string symbol,
        decimal price,
        decimal changePercent,
        decimal previousClose,
        decimal openPrice,
        decimal highPrice,
        decimal lowPrice,
        decimal volume,
        NormalizedNewsItem? news,
        string sentiment,
        decimal? marketCap,
        decimal? floatShares,
        decimal? institutionalOwnership,
        string priceSource,
        string volumeSource)
    {
        var output = new UnifiedMarketData
        {
            Symbol = symbol,
            Price = Math.Round(Math.Max(0m, price), 2),
            ChangePercent = Math.Round(changePercent, 2),
            Volume = Math.Round(Math.Max(0m, volume), 0),
            News = news,
            Sentiment = sentiment,
            MarketCap = marketCap,
            FloatShares = floatShares,
            InstitutionalOwnership = institutionalOwnership,
            Quote = new QuoteResponse
            {
                CurrentPrice = Math.Round(Math.Max(0m, price), 2),
                PreviousClose = Math.Round(Math.Max(0.01m, previousClose), 2),
                OpenPrice = Math.Round(Math.Max(0.01m, openPrice), 2),
                High = Math.Round(Math.Max(0.01m, highPrice), 2),
                Low = Math.Round(Math.Max(0.01m, lowPrice), 2),
                Volume = Math.Round(Math.Max(0m, volume), 0),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            },
            PriceSource = priceSource,
            VolumeSource = volumeSource
        };

        _lastKnownBySymbol[symbol] = output;
        return output;
    }

    private static string ResolveSentiment(NormalizedNewsItem? news, decimal changePercent)
    {
        if (news is not null)
        {
            return news.Sentiment;
        }

        return changePercent > 0m ? "BULLISH" : "BEARISH";
    }

    public async Task<UnifiedMarketData?> GetUnifiedMarketData(string symbol, CancellationToken cancellationToken = default)
    {
        return await GetUnifiedMarketDataAsync(symbol, includeNews: true, cancellationToken);
    }

    public MarketDataHealthMetrics GetHealthMetrics()
    {
        return new MarketDataHealthMetrics
        {
            TotalApiCalls = Interlocked.Read(ref _totalApiCalls),
            SuccessfulCalls = Interlocked.Read(ref _successfulCalls),
            FailedCalls = Interlocked.Read(ref _failedCalls),
            FallbackUsed = Interlocked.Read(ref _fallbackUsed),
            Errors = Interlocked.Read(ref _errors),
            ProviderCount = _providers.Count
        };
    }

    public async Task PersistProviderPerformanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = CreateProviderPerformanceSnapshot();
        await _providerStatsFileGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_providerStatsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(_providerStatsFilePath);
            await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);
            _logger.LogInformation("[API] STATS_SAVED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist provider performance snapshot.");
        }
        finally
        {
            _providerStatsFileGate.Release();
        }
    }

    public void MarkTopOpportunity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _forceFreshSymbols[symbol.Trim().ToUpperInvariant()] = DateTimeOffset.UtcNow;
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

    private bool ShouldSkipProvider(string providerName, DateTimeOffset now)
    {
        if (_disabledProviders.TryGetValue(providerName, out var disabledUntil))
        {
            if (now.UtcDateTime < disabledUntil)
            {
                return true;
            }

            _disabledProviders.TryRemove(providerName, out _);
            _providerFailureCounts[providerName] = 0;
            _logger.LogInformation("[API] RE-ENABLED -> {Provider}", providerName);
        }

        return false;
    }

    private List<(IQuoteProvider Provider, double Score)> GetRankedProviders(DateTimeOffset now)
    {
        var ranked = new List<(IQuoteProvider Provider, double Score)>(_providers.Count);
        foreach (var provider in _providers)
        {
            var providerName = provider.GetType().Name;
            if (ShouldSkipProvider(providerName, now))
            {
                continue;
            }

            var stats = _providerStats.GetOrAdd(providerName, name => new ProviderStats { Provider = name });
            var currentScore = (double)ComputeProviderScore(stats);
            var oldScore = _smoothedScores.GetOrAdd(providerName, currentScore);
            var smoothedScore = (oldScore * 0.8d) + (currentScore * 0.2d);
            _smoothedScores[providerName] = smoothedScore;
            ranked.Add((provider, smoothedScore));
        }

        return ranked
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Provider.GetType().Name, StringComparer.Ordinal)
            .ToList();
    }

    private static decimal ComputeProviderScore(ProviderStats stats)
    {
        var successReward = stats.SuccessRate;
        var failurePenalty = stats.FailedCalls * 8m;
        var rateLimitPenalty = stats.RateLimitHits * 35m;
        var latencyReward = stats.AverageLatencyMs <= 0m
            ? 20m
            : Math.Max(0m, 200m - stats.AverageLatencyMs) / 4m;
        var explorationBonus = stats.TotalCalls < 3 ? 10m : 0m;

        return successReward + latencyReward + explorationBonus - failurePenalty - rateLimitPenalty;
    }

    private void UpdateProviderStats(string providerName, bool success, long latencyMs, long rateLimitHits)
    {
        var stats = _providerStats.GetOrAdd(providerName, name => new ProviderStats { Provider = name });
        lock (stats)
        {
            stats.TotalCalls++;
            stats.TotalLatencyMs += Math.Max(0, latencyMs);
            if (success)
            {
                stats.SuccessCalls++;
            }
            else
            {
                stats.FailedCalls++;
            }

            if (rateLimitHits > 0)
            {
                stats.RateLimitHits += rateLimitHits;
            }
        }
    }

    private static int ResolveUnifiedTtlSeconds(UnifiedMarketData result)
    {
        var highVolatility = Math.Abs(result.ChangePercent) >= 3m;
        return highVolatility ? 4 : 9;
    }

    private void TryLoadProviderPerformanceState()
    {
        try
        {
            if (!File.Exists(_providerStatsFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_providerStatsFilePath);
            var snapshot = JsonSerializer.Deserialize<ProviderPerformanceSnapshot>(json, _jsonOptions);
            if (snapshot is null)
            {
                return;
            }

            if (snapshot.SmoothedScores is not null)
            {
                foreach (var entry in snapshot.SmoothedScores)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key))
                    {
                        continue;
                    }

                    _smoothedScores[entry.Key] = entry.Value;
                }
            }

            if (snapshot.ProviderStats is not null)
            {
                foreach (var entry in snapshot.ProviderStats)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                    {
                        continue;
                    }

                    _providerStats[entry.Key] = entry.Value;
                }
            }

            _logger.LogInformation("[API] STATS_LOADED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider performance snapshot load skipped (file missing/corrupt/unreadable).");
        }
    }

    private ProviderPerformanceSnapshot CreateProviderPerformanceSnapshot()
    {
        var statsSnapshot = new Dictionary<string, ProviderStats>(StringComparer.Ordinal);
        foreach (var entry in _providerStats)
        {
            var source = entry.Value;
            lock (source)
            {
                statsSnapshot[entry.Key] = new ProviderStats
                {
                    Provider = source.Provider,
                    TotalCalls = source.TotalCalls,
                    SuccessCalls = source.SuccessCalls,
                    FailedCalls = source.FailedCalls,
                    RateLimitHits = source.RateLimitHits,
                    TotalLatencyMs = source.TotalLatencyMs
                };
            }
        }

        var scoreSnapshot = _smoothedScores.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ProviderPerformanceSnapshot
        {
            SmoothedScores = scoreSnapshot,
            ProviderStats = statsSnapshot
        };
    }

    private sealed class ProviderPerformanceSnapshot
    {
        public Dictionary<string, double> SmoothedScores { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, ProviderStats> ProviderStats { get; set; } = new(StringComparer.Ordinal);
    }
}

