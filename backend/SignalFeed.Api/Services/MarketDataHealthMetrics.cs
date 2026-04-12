namespace SignalFeed.Api.Services;

public sealed class MarketDataHealthMetrics
{
    public long TotalApiCalls { get; set; }

    public long SuccessfulCalls { get; set; }

    public long FailedCalls { get; set; }

    public long FallbackUsed { get; set; }

    public long Errors { get; set; }

    public int ProviderCount { get; set; }
}
