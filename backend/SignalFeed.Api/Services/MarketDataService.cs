using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
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

    private readonly FinnhubService _finnhubService;
    private readonly PolygonService _polygonService;
    private readonly NewsAggregationService _newsAggregationService;
    private readonly FmpService _fmpService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MarketDataService> _logger;
    private readonly ConcurrentDictionary<string, UnifiedMarketData> _lastKnownBySymbol = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _forceFreshSymbols = new(StringComparer.Ordinal);

    public MarketDataService(
        FinnhubService finnhubService,
        PolygonService polygonService,
        NewsAggregationService newsAggregationService,
        FmpService fmpService,
        IMemoryCache cache,
        ILogger<MarketDataService> logger)
    {
        _finnhubService = finnhubService;
        _polygonService = polygonService;
        _newsAggregationService = newsAggregationService;
        _fmpService = fmpService;
        _cache = cache;
        _logger = logger;
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
        var finnhubQuoteTask = _finnhubService.GetQuoteAsync(normalizedSymbol);
        var polygonSnapshotTask = _polygonService.GetSnapshotAsync(normalizedSymbol, cancellationToken);
        var polygonAggregateTask = _polygonService.GetPreviousAggregateAsync(normalizedSymbol, cancellationToken);
        await Task.WhenAll(finnhubQuoteTask, polygonSnapshotTask, polygonAggregateTask);

        var finnhubQuote = finnhubQuoteTask.Result;
        var polygonSnapshot = polygonSnapshotTask.Result;
        var polygonAggregate = polygonAggregateTask.Result;

        var polygonPrice = polygonSnapshot?.LastTrade?.Price ?? 0m;
        if (polygonPrice <= 0m)
        {
            polygonPrice = polygonSnapshot?.Day?.Close ?? 0m;
        }

        var polygonChangePercent = polygonSnapshot?.TodaysChangePercent;
        var polygonVolume = polygonSnapshot?.Day?.Volume ?? 0m;

        var finnhubPrice = finnhubQuote?.CurrentPrice ?? 0m;
        var finnhubPreviousClose = finnhubQuote?.PreviousClose ?? 0m;

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
        else if (finnhubPrice > 0m && finnhubPreviousClose > 0m)
        {
            currentPrice = finnhubPrice;
            previousClose = finnhubPreviousClose;
            changePercent = Math.Round(((currentPrice - previousClose) / previousClose) * 100m, 2);
            openPrice = finnhubQuote?.OpenPrice ?? previousClose;
            highPrice = finnhubQuote?.High ?? currentPrice;
            lowPrice = finnhubQuote?.Low ?? currentPrice;
            priceSource = "FINNHUB";
            _logger.LogInformation("Finnhub fallback used for {Symbol}.", normalizedSymbol);
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

        if (usePolygonVolume)
        {
            volume = polygonVolume;
            volumeSource = "POLYGON";
        }
        else
        {
            volume = finnhubQuote?.Volume ?? polygonAggregate?.Volume ?? 0m;
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

    private static int ResolveUnifiedTtlSeconds(UnifiedMarketData result)
    {
        var highVolatility = Math.Abs(result.ChangePercent) >= 3m;
        return highVolatility ? 4 : 9;
    }
}
