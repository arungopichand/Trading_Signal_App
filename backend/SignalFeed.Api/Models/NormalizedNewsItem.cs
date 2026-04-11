namespace SignalFeed.Api.Models;

public sealed class NormalizedNewsItem
{
    public string Symbol { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTimeOffset Datetime { get; set; }

    public decimal SentimentScore { get; set; }

    public string Sentiment { get; set; } = "NEUTRAL";

    public string Category { get; set; } = "market commentary";
}
