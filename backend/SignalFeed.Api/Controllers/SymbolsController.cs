using Microsoft.AspNetCore.Mvc;
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
    public async Task<ActionResult<IReadOnlyList<string>>> GetSymbols(CancellationToken cancellationToken)
    {
        var symbols = await _symbolUniverseService.GetUniverseAsync(cancellationToken);
        return Ok(symbols);
    }
}
