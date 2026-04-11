using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/market")]
[Produces("application/json")]
public sealed class MarketController : ControllerBase
{
    private readonly IMarketDataService _marketDataService;
    private readonly decimal _volumeSpikeThreshold;

    public MarketController(IMarketDataService marketDataService, IConfiguration configuration)
    {
        _marketDataService = marketDataService;
        _volumeSpikeThreshold = Math.Max(100_000m, configuration.GetValue<decimal?>("Scanner:VolumeSpikeThreshold") ?? 500_000m);
    }

    [HttpGet("unified/{symbol}")]
    public async Task<IActionResult> GetUnified(string symbol, CancellationToken cancellationToken)
    {
        var data = await _marketDataService.GetUnifiedMarketData(symbol, cancellationToken);
        if (data is null)
        {
            return NotFound();
        }

        return Ok(data);
    }

    [HttpGet("analyze/{symbol}")]
    public async Task<IActionResult> AnalyzeSymbol(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new { error = "Symbol is required." });
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var data = await _marketDataService.GetUnifiedMarketData(normalizedSymbol, cancellationToken);
        if (data is null)
        {
            return NotFound(new { error = $"No market data found for {normalizedSymbol}." });
        }

        var strongMove = Math.Abs(data.ChangePercent) > 2m;
        var volumeSpike = data.Volume > _volumeSpikeThreshold;
        var hasNews = data.News is not null;
        var bullishNews = string.Equals(data.Sentiment, "BULLISH", StringComparison.OrdinalIgnoreCase);
        var bearishNews = string.Equals(data.Sentiment, "BEARISH", StringComparison.OrdinalIgnoreCase);

        var momentumScore = strongMove ? Math.Round(Math.Abs(data.ChangePercent) * 15m, 2) : 0m;
        var volumeScore = volumeSpike ? 30m : 0m;
        var newsScore = bullishNews || bearishNews ? 25m : 0m;
        var totalScore = momentumScore + volumeScore + newsScore;

        var signalType = strongMove && volumeSpike
            ? "SPIKE"
            : bullishNews
                ? "BULLISH"
                : bearishNews
                    ? "BEARISH"
                    : "TRENDING";

        var confidence = totalScore > 100m
            ? "HIGH"
            : totalScore > 70m
                ? "MEDIUM"
                : "LOW";

        var reason = new List<string>();
        if (strongMove)
        {
            reason.Add("Strong price move");
        }

        if (volumeSpike)
        {
            reason.Add("Volume spike");
        }

        if (bullishNews)
        {
            reason.Add("Positive news");
        }

        if (bearishNews)
        {
            reason.Add("Negative news");
        }

        return Ok(new
        {
            symbol = normalizedSymbol,
            data,
            factors = new
            {
                strongMove,
                volumeSpike,
                hasNews,
                bullishNews,
                bearishNews
            },
            scores = new
            {
                momentumScore,
                volumeScore,
                newsScore,
                totalScore
            },
            signalType,
            confidence,
            reason
        });
    }
}
