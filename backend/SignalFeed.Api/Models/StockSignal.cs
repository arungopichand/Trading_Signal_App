namespace SignalFeed.Api.Models;

public class StockSignal
{
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal ChangePercent { get; set; }

    public string SignalType { get; set; } = string.Empty;

    public decimal ActivityScore { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string SignalReason { get; set; } = string.Empty;

    public DateTimeOffset ScannedAt { get; set; }
}
