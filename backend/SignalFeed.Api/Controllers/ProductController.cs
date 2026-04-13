using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Models;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/product")]
[Produces("application/json")]
public sealed class ProductController : ControllerBase
{
    private readonly FeedService _feedService;
    private readonly SimulationSignalService _simulationSignalService;

    public ProductController(FeedService feedService, SimulationSignalService simulationSignalService)
    {
        _feedService = feedService;
        _simulationSignalService = simulationSignalService;
    }

    [HttpGet("alerts")]
    public ActionResult<object> GetAlerts(
        [FromQuery] decimal? scoreThreshold = null,
        [FromQuery] string? pinned = null,
        [FromQuery] int limit = 20)
    {
        var threshold = Math.Clamp(scoreThreshold ?? 90m, 1m, 500m);
        var max = Math.Clamp(limit, 1, 100);
        var pinnedSymbols = ParsePinnedSymbols(pinned);
        var now = DateTimeOffset.UtcNow;

        var alerts = _feedService.GetLatest(200)
            .Select(item =>
            {
                var reasons = new List<string>();
                var score = item.Score > 0 ? item.Score : item.ActivityScore;
                if (score >= threshold)
                {
                    reasons.Add("score_threshold");
                }

                if (pinnedSymbols.Contains(item.Symbol))
                {
                    reasons.Add("pinned_symbol");
                }

                return new
                {
                    item,
                    score,
                    reasons
                };
            })
            .Where(entry => entry.reasons.Count > 0)
            .OrderByDescending(entry => entry.score)
            .ThenByDescending(entry => entry.item.Timestamp)
            .Take(max)
            .Select(entry => new
            {
                symbol = entry.item.Symbol,
                signalType = entry.item.SignalType,
                score = entry.score,
                changePercent = entry.item.ChangePercent,
                source = entry.item.Source,
                reasons = entry.reasons,
                ageSeconds = Math.Max(0, (int)(now - entry.item.Timestamp).TotalSeconds)
            })
            .ToList();

        return Ok(new
        {
            threshold,
            pinnedSymbols = pinnedSymbols.ToArray(),
            alertCount = alerts.Count,
            alerts
        });
    }

    [HttpGet("session-summary")]
    public ActionResult<object> GetSessionSummary([FromQuery] int top = 10)
    {
        var take = Math.Clamp(top, 1, 50);
        var latest = _feedService.GetLatest(200);

        var topSignals = latest
            .OrderByDescending(item => item.Score > 0 ? item.Score : item.ActivityScore)
            .ThenByDescending(item => item.Timestamp)
            .Take(take)
            .Select(item => new
            {
                symbol = item.Symbol,
                signalType = item.SignalType,
                score = item.Score > 0 ? item.Score : item.ActivityScore,
                changePercent = item.ChangePercent,
                source = item.Source,
                timestamp = item.Timestamp
            })
            .ToList();

        var bestOpportunities = latest
            .Where(item => item.IsTopOpportunity)
            .OrderByDescending(item => item.Timestamp)
            .Take(5)
            .Select(item => new
            {
                symbol = item.Symbol,
                score = item.Score > 0 ? item.Score : item.ActivityScore,
                reason = item.Reason,
                source = item.Source,
                timestamp = item.Timestamp
            })
            .ToList();

        var signalCounts = latest
            .GroupBy(item => item.SignalType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var sourceCounts = latest
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Source) ? "UNKNOWN" : item.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow,
            totalSignals = latest.Count,
            topSignals,
            bestOpportunities,
            signalCounts,
            sourceCounts
        });
    }

    [HttpGet("simulator-summary")]
    public ActionResult<object> GetSimulatorSummary()
    {
        return Ok(new
        {
            stats = _simulationSignalService.GetStatsSnapshot(),
            performance = _simulationSignalService.GetPerformanceSnapshot(),
            timestamp = DateTimeOffset.UtcNow
        });
    }

    private static HashSet<string> ParsePinnedSymbols(string? pinned)
    {
        if (string.IsNullOrWhiteSpace(pinned))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return pinned
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
