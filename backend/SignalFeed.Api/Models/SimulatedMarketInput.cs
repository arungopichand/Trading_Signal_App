namespace SignalFeed.Api.Models;

public sealed class SimulatedMarketInput
{
    public string Symbol { get; set; } = string.Empty;

    public decimal CurrentPrice { get; set; }

    public decimal PreviousClose { get; set; }

    public decimal OpenPrice { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Volume { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string Source { get; set; } = "SIM";

    public string Url { get; set; } = string.Empty;

    public string Sentiment { get; set; } = "NEUTRAL";

    public string Category { get; set; } = "market commentary";

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
