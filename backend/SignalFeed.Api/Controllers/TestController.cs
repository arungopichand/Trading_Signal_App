using Microsoft.AspNetCore.Mvc;
using SignalFeed.Api.Services;

namespace SignalFeed.Api.Controllers;

[ApiController]
[Route("api/test")]
public class TestController : ControllerBase
{
    private readonly FinnhubService _service;

    public TestController(FinnhubService service)
    {
        _service = service;
    }

    [HttpGet("news")]
    public async Task<IActionResult> GetNews()
    {
        var data = await _service.GetNewsAsync();
        return Ok(data);
    }

    [HttpGet("quote/{symbol}")]
    public async Task<IActionResult> GetQuote(string symbol)
    {
        var data = await _service.GetQuoteAsync(symbol);
        return Ok(data);
    }
}