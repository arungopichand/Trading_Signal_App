using Microsoft.AspNetCore.SignalR;

namespace SignalFeed.Api.Hubs;

public sealed class FeedHub : Hub
{
    private readonly ILogger<FeedHub> _logger;

    public FeedHub(ILogger<FeedHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
