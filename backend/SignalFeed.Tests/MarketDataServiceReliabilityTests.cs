using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SignalFeed.Api.Services;
using SignalFeed.Tests.TestDoubles;

namespace SignalFeed.Tests;

public sealed class MarketDataServiceReliabilityTests
{
    [Fact]
    public async Task StreamFirst_UsesFreshWebSocket_WithoutRestCalls()
    {
        var ws = new FakeFinnhubWebSocketService();
        ws.SetPrice("AAPL", 189.10m, volume: 200);

        var finnhubHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"c\":180,\"pc\":179,\"h\":181,\"l\":178,\"o\":179,\"v\":1000,\"t\":1710000000}")));
        var polygonHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"OK\",\"results\":[{\"o\":180,\"h\":181,\"l\":179,\"c\":180.5,\"v\":1000,\"t\":1710000000000}]}")));
        var sut = CreateSut(ws, finnhubHandler, polygonHandler, null, null);

        var quote = await sut.GetQuoteAsync("AAPL");

        Assert.NotNull(quote);
        Assert.Equal("WebSocket", quote!.Provider);
        Assert.Equal(189.10m, quote.CurrentPrice);
        Assert.Equal(0, finnhubHandler.TotalCalls);
        Assert.Equal(0, polygonHandler.TotalCalls);
    }

    [Fact]
    public async Task FallsBackToPolygon_WhenFinnhubFails()
    {
        var ws = new FakeFinnhubWebSocketService();
        var finnhubHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}")));
        var polygonHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"OK\",\"results\":[{\"o\":189,\"h\":191,\"l\":188,\"c\":190,\"v\":1200,\"t\":1710000000000}]}")));
        var sut = CreateSut(ws, finnhubHandler, polygonHandler, null, null);

        var quote = await sut.GetQuoteAsync("MSFT");

        Assert.NotNull(quote);
        Assert.Equal(nameof(PolygonService), quote!.Provider);
        Assert.True(finnhubHandler.TotalCalls >= 1);
        Assert.True(polygonHandler.TotalCalls >= 1);
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsLastKnown_AsFallbackAndStale()
    {
        var ws = new FakeFinnhubWebSocketService();
        ws.SetPrice("NVDA", 920m, volume: 500);
        var shouldFailProviders = false;

        var finnhubHandler = new RouteHttpMessageHandler((_, _) =>
        {
            if (shouldFailProviders)
            {
                return Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}"));
            }

            return Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"c\":919,\"pc\":910,\"h\":922,\"l\":905,\"o\":912,\"v\":2000,\"t\":1710000000}"));
        });
        var polygonHandler = new RouteHttpMessageHandler((_, _) =>
        {
            if (shouldFailProviders)
            {
                return Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}"));
            }

            return Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"OK\",\"results\":[{\"o\":910,\"h\":925,\"l\":900,\"c\":920,\"v\":1800,\"t\":1710000000000}]}"));
        });
        var sut = CreateSut(ws, finnhubHandler, polygonHandler, null, null);

        _ = await sut.GetUnifiedMarketDataAsync("NVDA", includeNews: false);

        ws.Clear("NVDA");
        shouldFailProviders = true;
        sut.MarkTopOpportunity("NVDA");
        ((MemoryCache)typeof(MarketDataService)
            .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sut)!)
            .Compact(1.0);

        var result = await sut.GetUnifiedMarketDataAsync("NVDA", includeNews: false);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.True(result.IsStale);
        Assert.True(result.DataAgeSeconds >= 0);
    }

    [Fact]
    public async Task ConcurrentQuoteRequests_AreDeduplicated()
    {
        var ws = new FakeFinnhubWebSocketService();
        var callCounter = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        var finnhubHandler = new RouteHttpMessageHandler(async (request, _) =>
        {
            callCounter.AddOrUpdate("finnhub", 1, (_, current) => current + 1);
            await Task.Delay(100);
            return RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"c\":101,\"pc\":100,\"h\":102,\"l\":99,\"o\":100,\"v\":1000,\"t\":1710000000}");
        });

        var polygonHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"OK\",\"results\":[{\"o\":100,\"h\":102,\"l\":99,\"c\":101,\"v\":1000,\"t\":1710000000000}]}")));

        var sut = CreateSut(ws, finnhubHandler, polygonHandler, null, null);

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => sut.GetQuoteAsync("SPY"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.All(tasks, task => Assert.NotNull(task.Result));
        Assert.True(callCounter.TryGetValue("finnhub", out var calls));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HealthMetrics_ReportCacheAndStaleCounters()
    {
        var ws = new FakeFinnhubWebSocketService();
        ws.SetPrice("TSLA", 200m);
        var finnhubHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"c\":200,\"pc\":199,\"h\":201,\"l\":198,\"o\":199,\"v\":500,\"t\":1710000000}")));
        var polygonHandler = new RouteHttpMessageHandler((_, _) =>
            Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"OK\",\"results\":[{\"o\":199,\"h\":201,\"l\":198,\"c\":200,\"v\":500,\"t\":1710000000000}]}")));

        var sut = CreateSut(ws, finnhubHandler, polygonHandler, null, null);
        _ = await sut.GetUnifiedMarketDataAsync("TSLA", includeNews: false);
        _ = await sut.GetUnifiedMarketDataAsync("TSLA", includeNews: false); // cache hit

        var metrics = sut.GetHealthMetrics();
        Assert.True(metrics.CacheHitRatio > 0);
        Assert.True(metrics.ProviderSuccessCount >= 1);
        Assert.True(metrics.ProviderFailureCount >= 0);
    }

    private static MarketDataService CreateSut(
        FakeFinnhubWebSocketService ws,
        RouteHttpMessageHandler finnhubHandler,
        RouteHttpMessageHandler polygonHandler,
        RouteHttpMessageHandler? newsHandler,
        RouteHttpMessageHandler? fmpHandler,
        MarketDataService? reuseCacheFrom = null)
    {
        var memoryCache = reuseCacheFrom is null
            ? new MemoryCache(new MemoryCacheOptions())
            : (IMemoryCache)typeof(MarketDataService)
                .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(reuseCacheFrom)!;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FINNHUB_API_KEY"] = "test-finnhub-key",
                ["FINNHUB__APIKEY"] = "test-finnhub-key",
                ["POLYGON__APIKEY"] = "test-polygon-key",
                ["NEWSAPI__APIKEY"] = "test-news-key",
                ["FMP__APIKEY"] = "test-fmp-key"
            })
            .Build();
        var finnhubProviderState = new FinnhubProviderState();
        finnhubProviderState.Initialize("test-finnhub-key", NullLogger.Instance);

        var finnhub = new FinnhubService(
            new HttpClient(finnhubHandler) { BaseAddress = new Uri("https://finnhub.io/api/v1/") },
            config,
            memoryCache,
            finnhubProviderState,
            NullLogger<FinnhubService>.Instance);

        var polygon = new PolygonService(
            new HttpClient(polygonHandler) { BaseAddress = new Uri("https://api.polygon.io/") },
            config,
            memoryCache,
            NullLogger<PolygonService>.Instance);

        var news = new ExternalNewsApiService(
            new HttpClient(newsHandler ?? new RouteHttpMessageHandler((_, _) =>
                Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"articles\":[]}"))))
            { BaseAddress = new Uri("https://newsapi.org/") },
            config,
            NullLogger<ExternalNewsApiService>.Instance);

        var fmp = new FmpService(
            new HttpClient(fmpHandler ?? new RouteHttpMessageHandler((_, _) =>
                Task.FromResult(RouteHttpMessageHandler.Json(HttpStatusCode.OK, "[{\"mktCap\":1000000}]"))))
            { BaseAddress = new Uri("https://financialmodelingprep.com/") },
            config,
            memoryCache,
            NullLogger<FmpService>.Instance);

        return new MarketDataService(
            finnhub,
            polygon,
            news,
            fmp,
            ws,
            finnhubProviderState,
            new ProviderHealthTracker(),
            new ApiUsageTracker(),
            memoryCache,
            NullLogger<MarketDataService>.Instance);
    }
}
