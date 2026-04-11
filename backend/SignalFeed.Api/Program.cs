using Microsoft.Extensions.Hosting;
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

builder.Services.AddHttpClient<FinnhubService>(client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<SupabaseDataService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddScoped<SignalEngine>();
builder.Services.AddScoped<SymbolUniverseService>();
builder.Services.AddHostedService<SignalBackgroundService>();

var allowedOrigins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy("ScannerCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

var app = builder.Build();

app.UseCors("ScannerCors");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();
