using Microsoft.Extensions.Hosting;
using SignalFeed.Api.Hubs;
using SignalFeed.Api.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls("http://0.0.0.0:" + Environment.GetEnvironmentVariable("PORT"));
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
builder.Services.AddSingleton<ApiUsageTracker>();
builder.Services.AddSingleton<ApiKeyStatusProvider>();

builder.Services.AddHttpClient<FinnhubService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
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

builder.Services.AddHttpClient<PolygonService>(client =>
{
    client.BaseAddress = new Uri("https://api.polygon.io/");
    client.Timeout = TimeSpan.FromSeconds(15);
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
    client.Timeout = TimeSpan.FromSeconds(15);
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
    client.Timeout = TimeSpan.FromSeconds(15);
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
    client.Timeout = TimeSpan.FromSeconds(15);
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
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<IMarketDataService>(serviceProvider =>
    serviceProvider.GetRequiredService<MarketDataService>());
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<SignalEngine>();
builder.Services.AddSingleton<SimulationSignalService>();
builder.Services.AddHostedService<UniverseRefreshBackgroundService>();
builder.Services.AddHostedService<SignalBackgroundService>();
builder.Services.AddHostedService<FinnhubRealtimeStreamService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();
Console.WriteLine("DEPLOY VERSION: " + DateTime.UtcNow);

app.Services.GetRequiredService<ApiUsageTracker>().DiscoverApiServices(typeof(Program).Assembly);

static void WarnIfMissingKey(IConfiguration config, string envName, string sectionPath)
{
    var value = Environment.GetEnvironmentVariable(envName) ?? config[envName] ?? config[sectionPath];
    if (string.IsNullOrWhiteSpace(value))
    {
        Console.WriteLine($"WARN: {envName} missing. Service will continue with available sources.");
    }
}

WarnIfMissingKey(builder.Configuration, "FINNHUB__APIKEY", "Finnhub:ApiKey");
WarnIfMissingKey(builder.Configuration, "POLYGON__APIKEY", "Polygon:ApiKey");
WarnIfMissingKey(builder.Configuration, "NEWSAPI__APIKEY", "NewsApi:ApiKey");
WarnIfMissingKey(builder.Configuration, "FMP__APIKEY", "Fmp:ApiKey");

app.UseCors("AllowFrontend");
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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapHub<FeedHub>("/hubs/feed");

app.Run();
