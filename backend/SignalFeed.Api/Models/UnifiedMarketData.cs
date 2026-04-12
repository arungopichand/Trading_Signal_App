namespace SignalFeed.Api.Models;

public sealed class UnifiedMarketData
{
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal ChangePercent { get; set; }

    public decimal Volume { get; set; }

    public NormalizedNewsItem? News { get; set; }

    public string Sentiment { get; set; } = "NEUTRAL";

    public decimal? MarketCap { get; set; }

    public decimal? FloatShares { get; set; }

    public decimal? InstitutionalOwnership { get; set; }

    public QuoteResponse Quote { get; set; } = new();

    public string PriceSource { get; set; } = "FINNHUB";

    public string VolumeSource { get; set; } = "FINNHUB";

    public string SourceProvider { get; set; } = "FINNHUB";

    public bool IsCached { get; set; }

    public bool IsFallback { get; set; }

    public int DataAgeSeconds { get; set; }

    public bool IsStale { get; set; }
}
