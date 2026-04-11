using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Models;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/symbols")]
[Produces("application/json")]
public class SymbolsController : ControllerBase
{
    private readonly SymbolUniverseService _symbolUniverseService;

    public SymbolsController(SymbolUniverseService symbolUniverseService)
    {
        _symbolUniverseService = symbolUniverseService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TrackedSymbol>>> GetSymbols(CancellationToken cancellationToken)
    {
        var symbols = await _symbolUniverseService.GetSymbolsAsync(cancellationToken);
        return Ok(symbols);
    }
}
