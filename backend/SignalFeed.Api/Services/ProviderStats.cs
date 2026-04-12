namespace SignalFeed.Api.Services;

public sealed class ProviderStats
{
    public string Provider { get; set; } = string.Empty;

    public long TotalCalls { get; set; }

    public long SuccessCalls { get; set; }

    public long FailedCalls { get; set; }

    public long RateLimitHits { get; set; }

    public long TotalLatencyMs { get; set; }

    public decimal SuccessRate => TotalCalls == 0
        ? 0m
        : Math.Round((SuccessCalls * 100m) / TotalCalls, 2);

    public decimal AverageLatencyMs => TotalCalls == 0
        ? 0m
        : Math.Round(TotalLatencyMs / (decimal)TotalCalls, 2);
}
