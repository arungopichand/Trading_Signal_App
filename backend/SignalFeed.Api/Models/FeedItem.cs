namespace SignalFeed.Api.Models;

public sealed class FeedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal ChangePercent { get; set; }

    public string SignalType { get; set; } = "NEWS";

    public decimal Score { get; set; }

    public decimal ActivityScore { get; set; }

    public string Confidence { get; set; } = "LOW";

    public bool IsTopOpportunity { get; set; }

    public bool IsTrending { get; set; }

    public string Headline { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string Source { get; set; } = string.Empty;
}
