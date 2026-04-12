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

    public HealthController(
        ApiUsageTracker apiUsageTracker,
        ApiKeyStatusProvider apiKeyStatusProvider,
        IConfiguration configuration)
    {
        _apiUsageTracker = apiUsageTracker;
        _apiKeyStatusProvider = apiKeyStatusProvider;
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var usage = _apiUsageTracker.GetUsageSnapshot();
        var keys = _apiKeyStatusProvider.GetKeyStatus(_configuration);

        return Ok(new
        {
            apis = usage.Select(item => new
            {
                service = item.Service,
                baseUrl = string.IsNullOrWhiteSpace(item.BaseUrl) ? null : item.BaseUrl,
                calls = item.Calls,
                success = item.Success,
                failures = item.Failures
            }),
            keys,
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
