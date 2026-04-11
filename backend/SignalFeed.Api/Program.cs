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

builder.Services.AddHttpClient<FinnhubService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseProxy = false
});

builder.Services.AddHttpClient<SupabaseDataService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<SymbolUniverseService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<SignalEngine>();
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
Console.WriteLine("=== NEW DEPLOY VERSION ===");

app.UseCors("AllowFrontend");
app.MapGet("/", () => Results.Ok(new
{
    name = "Trading Signal API",
    status = "running",
    health = "/health",
    symbols = "/api/symbols",
    signals = "/api/signals/current",
    feed = "/api/feed",
    hub = "/hubs/feed"
}));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapHub<FeedHub>("/hubs/feed");

app.Run();
