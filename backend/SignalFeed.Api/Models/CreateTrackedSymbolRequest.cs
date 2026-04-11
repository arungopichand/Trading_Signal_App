namespace SignalFeed.Api.Models;

public class CreateTrackedSymbolRequest
{
    public string Symbol { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
