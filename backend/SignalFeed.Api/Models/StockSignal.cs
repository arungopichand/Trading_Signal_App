namespace SignalFeed.Api.Models;

public class StockSignal
{
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal ChangePercent { get; set; }

    public string SignalType { get; set; } = string.Empty;

    public decimal Score { get; set; }

    public decimal ActivityScore { get; set; }

    public string Confidence { get; set; } = "LOW";

    public bool IsTopOpportunity { get; set; }

    public bool IsTrending { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string SignalReason { get; set; } = string.Empty;

    public string Source { get; set; } = "Scanner";

    public string CountryCode { get; set; } = string.Empty;

    public decimal? FloatMillions { get; set; }

    public decimal? InstitutionalOwnershipPercent { get; set; }

    public decimal? MarketCapMillions { get; set; }

    public decimal? SessionVolumeMillions { get; set; }

    public decimal? RelativeVolume { get; set; }

    public int? GreenBars5m { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public DateTimeOffset ScannedAt { get; set; }
}
