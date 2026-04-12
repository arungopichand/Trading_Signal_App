using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/health")]
[Produces("application/json")]
public sealed class HealthController : ControllerBase
{
    private readonly ApiUsageTracker _apiUsageTracker;
    private readonly ApiKeyStatusProvider _apiKeyStatusProvider;
    private readonly IConfiguration _configuration;
    private readonly FeedService _feedService;
    private readonly MarketDataService _marketDataService;
    private readonly IFinnhubWebSocketService _finnhubWebSocketService;

    public HealthController(
        ApiUsageTracker apiUsageTracker,
        ApiKeyStatusProvider apiKeyStatusProvider,
        IConfiguration configuration,
        FeedService feedService,
        MarketDataService marketDataService,
        IFinnhubWebSocketService finnhubWebSocketService)
    {
        _apiUsageTracker = apiUsageTracker;
        _apiKeyStatusProvider = apiKeyStatusProvider;
        _configuration = configuration;
        _feedService = feedService;
        _marketDataService = marketDataService;
        _finnhubWebSocketService = finnhubWebSocketService;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var usage = _apiUsageTracker.GetUsageSnapshot();
        var keys = _apiKeyStatusProvider.GetKeyStatus(_configuration);
        var scanner = SignalBackgroundService.GetRuntimeSnapshot();
        var feed = _feedService.GetRuntimeDiagnostics();
        var symbolSources = _feedService.GetSourceDiagnostics(200);
        var marketData = _marketDataService.GetHealthMetrics();
        var stream = _finnhubWebSocketService.GetHealthSnapshot();

        return Ok(new
        {
            apis = usage.Select(item => new
            {
                service = item.Service,
                baseUrl = string.IsNullOrWhiteSpace(item.BaseUrl) ? null : item.BaseUrl,
                calls = item.Calls,
                success = item.Success,
                failures = item.Failures,
                rateLimitHits = item.RateLimitHits
            }),
            keys,
            scanner,
            feed,
            symbolSources,
            marketData,
            stream,
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
