namespace SignalFeed.Api.Models;

public class QuoteSnapshot
{
    public string Symbol { get; set; } = string.Empty;

    public decimal CurrentPrice { get; set; }

    public decimal PreviousClose { get; set; }

    public decimal DayHigh { get; set; }

    public decimal DayLow { get; set; }

    public decimal ChangePercent { get; set; }

    public DateTimeOffset ScannedAt { get; set; }
}
