using Microsoft.Extensions.Hosting;
using SignalFeed.Api.Hubs;
using SignalFeed.Api.Services;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

var aspNetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(aspNetCoreUrls))
{
    var port = Environment.GetEnvironmentVariable("PORT");
    if (int.TryParse(port, out var parsedPort) && parsedPort > 0)
    {
        builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
    }
    else
    {
        builder.WebHost.UseUrls("http://0.0.0.0:10000");
    }
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression();
builder.Services.AddSingleton<ApiUsageTracker>();
builder.Services.AddSingleton<ApiKeyStatusProvider>();
builder.Services.AddSingleton<FinnhubProviderState>();

static TimeSpan ResolveProviderTimeout(IConfiguration configuration, string providerKey, int defaultSeconds)
{
    var configured = configuration.GetValue<int?>($"Providers:{providerKey}:TimeoutSeconds") ?? defaultSeconds;
    return TimeSpan.FromSeconds(Math.Clamp(configured, 2, 20));
}

builder.Services.AddHttpClient<FinnhubService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "Finnhub", 8);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
})
.AddHttpMessageHandler(serviceProvider =>
{
    var tracker = serviceProvider.GetRequiredService<ApiUsageTracker>();
    var serviceName = nameof(FinnhubService);
    tracker.RegisterConfiguredService(serviceName, "https://finnhub.io/api/v1");
    return new ApiCallLoggingHandler(tracker, serviceProvider.GetRequiredService<ILoggerFactory>(), serviceName);
});
builder.Services.AddHttpClient<FinnhubProviderProbeService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "Finnhub", 8);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
});

builder.Services.AddHttpClient<PolygonService>(client =>
{
    client.BaseAddress = new Uri("https://api.polygon.io/");
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "Polygon", 6);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
})
.AddHttpMessageHandler(serviceProvider =>
{
    var tracker = serviceProvider.GetRequiredService<ApiUsageTracker>();
    var serviceName = nameof(PolygonService);
    tracker.RegisterConfiguredService(serviceName, "https://api.polygon.io");
    return new ApiCallLoggingHandler(tracker, serviceProvider.GetRequiredService<ILoggerFactory>(), serviceName);
});

builder.Services.AddHttpClient<ExternalNewsApiService>(client =>
{
    client.BaseAddress = new Uri("https://newsapi.org/");
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "NewsApi", 6);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
})
.AddHttpMessageHandler(serviceProvider =>
{
    var tracker = serviceProvider.GetRequiredService<ApiUsageTracker>();
    var serviceName = nameof(ExternalNewsApiService);
    tracker.RegisterConfiguredService(serviceName, "https://newsapi.org");
    return new ApiCallLoggingHandler(tracker, serviceProvider.GetRequiredService<ILoggerFactory>(), serviceName);
});

builder.Services.AddHttpClient<FmpService>(client =>
{
    client.BaseAddress = new Uri("https://financialmodelingprep.com/");
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "Fmp", 6);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
})
.AddHttpMessageHandler(serviceProvider =>
{
    var tracker = serviceProvider.GetRequiredService<ApiUsageTracker>();
    var serviceName = nameof(FmpService);
    tracker.RegisterConfiguredService(serviceName, "https://financialmodelingprep.com");
    return new ApiCallLoggingHandler(tracker, serviceProvider.GetRequiredService<ILoggerFactory>(), serviceName);
});

builder.Services.AddHttpClient<SupabaseDataService>(client =>
{
    client.Timeout = ResolveProviderTimeout(builder.Configuration, "Supabase", 8);
})
.AddHttpMessageHandler(serviceProvider =>
{
    var tracker = serviceProvider.GetRequiredService<ApiUsageTracker>();
    var serviceName = nameof(SupabaseDataService);
    tracker.RegisterConfiguredService(serviceName);
    return new ApiCallLoggingHandler(tracker, serviceProvider.GetRequiredService<ILoggerFactory>(), serviceName);
});

builder.Services.AddSingleton<IQuoteProvider>(serviceProvider => serviceProvider.GetRequiredService<FinnhubService>());
builder.Services.AddSingleton<IQuoteProvider>(serviceProvider => serviceProvider.GetRequiredService<PolygonService>());
builder.Services.AddSingleton<IQuoteProvider>(serviceProvider => serviceProvider.GetRequiredService<FmpService>());

builder.Services.AddSingleton<SymbolUniverseService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<NewsAggregationService>();
builder.Services.AddSingleton<ProviderHealthTracker>();
builder.Services.Configure<FinnhubWebSocketOptions>(builder.Configuration.GetSection("FinnhubWebSocket"));
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<IMarketDataService>(serviceProvider =>
    serviceProvider.GetRequiredService<MarketDataService>());
builder.Services.AddSingleton<FinnhubQuoteStreamService>();
builder.Services.AddSingleton<IFinnhubWebSocketService>(serviceProvider =>
    serviceProvider.GetRequiredService<FinnhubQuoteStreamService>());
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<SignalEngine>();
builder.Services.AddSingleton<SimulationSignalService>();

var enableSignalScanner = builder.Configuration.GetValue<bool?>("ENABLE_SIGNAL_SCANNER") ?? true;
var enableUniverseRefresh = builder.Configuration.GetValue<bool?>("ENABLE_UNIVERSE_REFRESH") ?? true;
var enableFinnhubPriceStream = builder.Configuration.GetValue<bool?>("ENABLE_FINNHUB_PRICE_STREAM") ?? true;
var finnhubApiKey = FinnhubProviderState.ResolveApiKey(builder.Configuration);
var finnhubConfigured = !string.IsNullOrWhiteSpace(finnhubApiKey);

if (enableFinnhubPriceStream && finnhubConfigured)
{
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<FinnhubQuoteStreamService>());
}

if (enableUniverseRefresh)
{
    builder.Services.AddHostedService<UniverseRefreshBackgroundService>();
}

if (enableSignalScanner)
{
    builder.Services.AddHostedService<SignalBackgroundService>();
}

builder.Services.AddHostedService<FinnhubProviderProbeService>();

var allowedOrigins = ParseAllowedOrigins(
    Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
    ?? builder.Configuration["CORS_ALLOWED_ORIGINS"]
    ?? builder.Configuration["Cors:AllowedOrigins"]);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .SetIsOriginAllowed(origin => IsAllowedOrigin(origin, allowedOrigins))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
var finnhubProviderState = app.Services.GetRequiredService<FinnhubProviderState>();
finnhubProviderState.Initialize(finnhubApiKey, app.Logger);
if (!finnhubConfigured)
{
    app.Logger.LogCritical("FINNHUB KEY MISSING. WebSocket and Finnhub REST provider are disabled.");
}

app.UseResponseCompression();
app.UseCors("AllowFrontend");

app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    if (sw.ElapsedMilliseconds > 300)
    {
        app.Logger.LogWarning(
            "SLOW_REQUEST {Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            sw.ElapsedMilliseconds);
    }
});

app.MapGet("/", () => Results.Ok(new
{
    name = "Trading Signal API",
    status = "running",
    health = "/health",
    apiHealth = "/api/health",
    symbols = "/api/symbols",
    signals = "/api/signals/current",
    feed = "/api/feed",
    hub = "/hubs/feed"
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/health/finnhub", (FinnhubProviderState state, IFinnhubWebSocketService stream) =>
{
    var snapshot = state.GetSnapshot();
    var streamHealth = stream.GetHealthSnapshot();
    var overallStatus = !snapshot.Configured || !snapshot.Valid
        ? "DOWN"
        : streamHealth.IsConnected
            ? "UP"
            : "DEGRADED";

    return Results.Ok(new
    {
        configured = snapshot.Configured,
        valid = snapshot.Valid,
        wsConnected = streamHealth.IsConnected,
        lastError = snapshot.LastError,
        lastErrorKind = snapshot.LastErrorKind,
        lastSuccessSecondsAgo = snapshot.LastSuccessSecondsAgo,
        overallStatus
    });
});
app.MapGet("/health/stream", (IFinnhubWebSocketService stream, MarketDataService marketDataService) =>
{
    var streamHealth = stream.GetHealthSnapshot();
    var marketMetrics = marketDataService.GetHealthMetrics();
    return Results.Ok(new
    {
        status = streamHealth.IsConnected ? "connected" : "degraded",
        stream = streamHealth,
        marketData = new
        {
            staleDataReturnCount = marketMetrics.StaleDataReturnCount,
            staleDataRatePercent = marketMetrics.StaleDataRatePercent,
            websocketReconnectCount = marketMetrics.WebsocketReconnectCount,
            rateLimitHits = marketMetrics.RateLimitHits
        },
        timestamp = DateTimeOffset.UtcNow
    });
});
app.MapGet("/metrics/summary", (MarketDataService marketDataService, FeedService feedService) =>
{
    var marketData = marketDataService.GetHealthMetrics();
    var feed = feedService.GetRuntimeDiagnostics();
    var scanner = SignalBackgroundService.GetRuntimeSnapshot();

    return Results.Ok(new
    {
        provider_success_count = marketData.ProviderSuccessCount,
        provider_failure_count = marketData.ProviderFailureCount,
        rate_limit_hits = marketData.RateLimitHits,
        websocket_reconnect_count = marketData.WebsocketReconnectCount,
        cache_hit_ratio = marketData.CacheHitRatio,
        stale_data_return_count = marketData.StaleDataReturnCount,
        finnhub_success_count = marketData.FinnhubSuccessCount,
        finnhub_failure_count = marketData.FinnhubFailureCount,
        finnhub_key_invalid_count = marketData.FinnhubKeyInvalidCount,
        fallback_finnhub_skipped_count = marketData.FallbackFinnhubSkippedCount,
        fallback_polygon_used_count = marketData.FallbackPolygonUsedCount,
        fallback_cache_used_count = marketData.FallbackCacheUsedCount,
        latency = new
        {
            p50 = marketData.ProviderLatencyP50Ms,
            p95 = marketData.ProviderLatencyP95Ms,
            p99 = marketData.ProviderLatencyP99Ms
        },
        feed = new
        {
            broadcastCount = feed.BroadcastCount,
            averageBroadcastLatencyMs = feed.AverageBroadcastLatencyMs,
            sourceSwitchCount = feed.SourceSwitchCount
        },
        scanner = new
        {
            scanCycleCount = scanner.ScanCycleCount,
            lastScanDurationMs = scanner.LastScanDurationMs,
            averageScanDurationMs = scanner.AverageScanDurationMs
        },
        timestamp = DateTimeOffset.UtcNow
    });
});
app.MapControllers();
app.MapHub<FeedHub>("/hubs/feed");

app.Run();

static string[] ParseAllowedOrigins(string? configuredOrigins)
{
    var configured = (configuredOrigins ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    configured.Add("http://localhost:5173");
    configured.Add("http://127.0.0.1:5173");

    return configured
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static bool IsAllowedOrigin(string origin, IReadOnlyCollection<string> explicitOrigins)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return false;
    }

    if (explicitOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.Scheme is "http" or "https"
        && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase);
}
