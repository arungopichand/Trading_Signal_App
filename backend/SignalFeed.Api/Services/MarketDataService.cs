using System.Collections.Concurrent;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class MarketDataService : IMarketDataService
{
    private readonly FinnhubService _finnhubService;
    private readonly PolygonService _polygonService;
    private readonly NewsAggregationService _newsAggregationService;
    private readonly FmpService _fmpService;
    private readonly ILogger<MarketDataService> _logger;
    private readonly ConcurrentDictionary<string, UnifiedMarketData> _lastKnownBySymbol = new(StringComparer.Ordinal);

    public MarketDataService(
        FinnhubService finnhubService,
        PolygonService polygonService,
        NewsAggregationService newsAggregationService,
        FmpService fmpService,
        ILogger<MarketDataService> logger)
    {
        _finnhubService = finnhubService;
        _polygonService = polygonService;
        _newsAggregationService = newsAggregationService;
        _fmpService = fmpService;
        _logger = logger;
    }

    public async Task<UnifiedMarketData> GetUnifiedMarketDataAsync(
        string symbol,
        bool includeNews,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();

        var finnhubQuoteTask = _finnhubService.GetQuoteAsync(normalizedSymbol);
        var polygonSnapshotTask = _polygonService.GetSnapshotAsync(normalizedSymbol, cancellationToken);
        var polygonAggregateTask = _polygonService.GetPreviousAggregateAsync(normalizedSymbol, cancellationToken);
        var factorsTask = _fmpService.GetFactorsAsync(normalizedSymbol, cancellationToken);
        Task<NormalizedNewsItem?>? newsTask = includeNews
            ? _newsAggregationService.GetLatestNewsAsync(normalizedSymbol, cancellationToken)
            : null;

        var tasks = new List<Task> { finnhubQuoteTask, polygonSnapshotTask, polygonAggregateTask, factorsTask };
        if (newsTask is not null)
        {
            tasks.Add(newsTask);
        }

        await Task.WhenAll(tasks);

        var finnhubQuote = finnhubQuoteTask.Result;
        var polygonSnapshot = polygonSnapshotTask.Result;
        var polygonAggregate = polygonAggregateTask.Result;
        var factors = factorsTask.Result;
        var news = newsTask?.Result;

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
            var fallbackSentiment = ResolveSentiment(news, changePercent);
            return BuildAndCache(
                normalizedSymbol,
                currentPrice,
                changePercent,
                previousClose,
                openPrice,
                highPrice,
                lowPrice,
                volume,
                news,
                fallbackSentiment,
                cached.MarketCap ?? factors?.MarketCap,
                cached.FloatShares ?? factors?.FloatShares,
                cached.InstitutionalOwnership ?? factors?.InstitutionalOwnership,
                priceSource: "CACHED_FALLBACK",
                volumeSource: cached.VolumeSource);
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

        var sentiment = ResolveSentiment(news, changePercent);
        return BuildAndCache(
            normalizedSymbol,
            currentPrice,
            changePercent,
            previousClose,
            openPrice,
            highPrice,
            lowPrice,
            volume,
            news,
            sentiment,
            factors?.MarketCap,
            factors?.FloatShares,
            factors?.InstitutionalOwnership,
            priceSource,
            volumeSource);
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
}
