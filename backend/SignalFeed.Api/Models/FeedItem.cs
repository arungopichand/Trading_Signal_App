namespace SignalFeed.Api.Models;

public sealed class FeedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Symbol { get; set; } = string.Empty;

    public string CountryCode { get; set; } = "US";

    public decimal Price { get; set; }

    public string PriceRange { get; set; } = string.Empty;

    public decimal ChangePercent { get; set; }

    public string SignalType { get; set; } = "NEWS";

    public decimal Score { get; set; }

    public decimal ActivityScore { get; set; }

    public string Confidence { get; set; } = "LOW";

    public string TradeReadiness { get; set; } = "WATCH";

    public bool IsTopOpportunity { get; set; }

    public bool IsTrending { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public decimal? FloatShares { get; set; }

    public decimal? InstitutionalOwnership { get; set; }

    public decimal? MarketCap { get; set; }

    public decimal? Volume { get; set; }

    public List<string> Flags { get; set; } = [];

    public decimal? VolumeRatio { get; set; }

    public decimal? Momentum { get; set; }

    public string Sentiment { get; set; } = "NEUTRAL";

    public decimal? Acceleration { get; set; }

    public decimal? GapPercent { get; set; }

    public string NewsCategory { get; set; } = string.Empty;

    public int RepeatCount { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? MomentumDetectedAt { get; set; }

    public string Source { get; set; } = string.Empty;
}
