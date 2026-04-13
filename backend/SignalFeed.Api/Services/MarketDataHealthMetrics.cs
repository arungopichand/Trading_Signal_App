namespace SignalFeed.Api.Services;

public sealed class MarketDataHealthMetrics
{
    public long TotalApiCalls { get; set; }

    public long SuccessfulCalls { get; set; }

    public long FailedCalls { get; set; }

    public long FallbackUsed { get; set; }

    public long Errors { get; set; }

    public long ProviderSuccessCount { get; set; }

    public long ProviderFailureCount { get; set; }

    public long RateLimitHits { get; set; }

    public long WebsocketReconnectCount { get; set; }

    public double CacheHitRatio { get; set; }

    public long FinnhubSuccessCount { get; set; }

    public long FinnhubFailureCount { get; set; }

    public long FinnhubKeyInvalidCount { get; set; }

    public long FallbackFinnhubSkippedCount { get; set; }

    public long FallbackPolygonUsedCount { get; set; }

    public long FallbackCacheUsedCount { get; set; }

    public long StaleDataReturnCount { get; set; }

    public double StaleDataRatePercent { get; set; }

    public double ProviderLatencyP50Ms { get; set; }

    public double ProviderLatencyP95Ms { get; set; }

    public double ProviderLatencyP99Ms { get; set; }

    public int ProviderCount { get; set; }

    public IReadOnlyDictionary<string, ProviderHealthSnapshot> Providers { get; set; } =
        new Dictionary<string, ProviderHealthSnapshot>(StringComparer.Ordinal);
}
