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

    public long StaleDataReturnCount { get; set; }

    public int ProviderCount { get; set; }

    public IReadOnlyDictionary<string, ProviderHealthSnapshot> Providers { get; set; } =
        new Dictionary<string, ProviderHealthSnapshot>(StringComparer.Ordinal);
}
