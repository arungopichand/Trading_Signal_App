using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Models;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/signals")]
[Produces("application/json")]
public class SignalController : ControllerBase
{
    [HttpGet("current")]
    public ActionResult<IReadOnlyList<StockSignal>> GetCurrentSignals()
    {
        return Ok(SignalBackgroundService.CachedSignals);
    }
}
