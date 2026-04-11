namespace SignalFeed.Api.Models;

public class StockSignal
{
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public string PriceRange { get; set; } = string.Empty;

    public decimal ChangePercent { get; set; }

    public string SignalType { get; set; } = string.Empty;

    public decimal Score { get; set; }

    public decimal ActivityScore { get; set; }

    public string Confidence { get; set; } = "LOW";

    public string TradeReadiness { get; set; } = "WATCH";

    public bool IsTopOpportunity { get; set; }

    public bool IsTrending { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string SignalReason { get; set; } = string.Empty;

    public decimal? FloatShares { get; set; }

    public decimal? InstitutionalOwnership { get; set; }

    public decimal? MarketCap { get; set; }

    public decimal? Volume { get; set; }

    public List<string> Flags { get; set; } = [];

    public string Source { get; set; } = "Scanner";

    public string CountryCode { get; set; } = string.Empty;

    public decimal? VolumeRatio { get; set; }

    public decimal? Momentum { get; set; }

    public string Sentiment { get; set; } = "NEUTRAL";

    public decimal? Acceleration { get; set; }

    public decimal? GapPercent { get; set; }

    public string NewsCategory { get; set; } = string.Empty;

    public int RepeatCount { get; set; }

    public decimal? RelativeVolume { get; set; }

    public int? GreenBars5m { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public DateTimeOffset? MomentumDetectedAt { get; set; }

    public DateTimeOffset ScannedAt { get; set; }
}
