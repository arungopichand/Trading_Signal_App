namespace SignalFeed.Api.Models;

public sealed class StreamPriceSnapshot
{
    public string Symbol { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public decimal Volume { get; init; }
    public DateTimeOffset TradeTimestampUtc { get; init; }
    public DateTimeOffset ReceivedTimestampUtc { get; init; }
    public string SourceProvider { get; init; } = "FinnhubWebSocket";

    public int AgeSeconds(DateTimeOffset nowUtc)
    {
        var age = nowUtc - ReceivedTimestampUtc;
        return age <= TimeSpan.Zero ? 0 : (int)Math.Floor(age.TotalSeconds);
    }
}
