using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Models;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/feed")]
[Produces("application/json")]
public sealed class FeedController : ControllerBase
{
    private readonly FeedService _feedService;
    private readonly SimulationSignalService _simulationSignalService;

    public FeedController(FeedService feedService, SimulationSignalService simulationSignalService)
    {
        _feedService = feedService;
        _simulationSignalService = simulationSignalService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<FeedItem>> GetFeed([FromQuery] int? limit = null)
    {
        return Ok(_feedService.GetLatest(limit ?? 100));
    }

    [HttpGet("simulate")]
    public async Task<ActionResult<IReadOnlyList<FeedItem>>> GetSimulatedFeed(CancellationToken cancellationToken)
    {
        var items = await _simulationSignalService.GenerateFeedBatchAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("sim-stats")]
    public ActionResult<SimulationStatsSnapshot> GetSimulationStats()
    {
        var snapshot = _simulationSignalService.GetStatsSnapshot();
        return Ok(snapshot);
    }
}
