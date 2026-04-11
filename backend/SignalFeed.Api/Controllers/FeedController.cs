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

    public FeedController(FeedService feedService)
    {
        _feedService = feedService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<FeedItem>> GetFeed()
    {
        return Ok(_feedService.GetLatest());
    }
}
