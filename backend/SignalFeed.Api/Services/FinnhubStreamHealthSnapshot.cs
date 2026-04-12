namespace SignalFeed.Api.Services;

public sealed class FinnhubStreamHealthSnapshot
{
    public bool IsConnected { get; set; }

    public long ReconnectCount { get; set; }

    public int ActiveSubscriptionCount { get; set; }

    public int CachedPriceCount { get; set; }

    public DateTimeOffset? RateLimitedUntilUtc { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}
