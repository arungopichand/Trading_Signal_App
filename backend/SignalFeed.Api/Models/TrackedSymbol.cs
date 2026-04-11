namespace SignalFeed.Api.Models;

public class TrackedSymbol
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
