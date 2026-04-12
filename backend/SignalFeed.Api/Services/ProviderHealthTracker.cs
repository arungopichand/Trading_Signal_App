using System.Collections.Concurrent;

namespace SignalFeed.Api.Services;

public sealed class ProviderHealthTracker
{
    private enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }

    private sealed class ProviderState
    {
        public long TotalCalls;
        public long SuccessCalls;
        public long FailureCalls;
        public long TotalLatencyMs;
        public long RateLimitHits;
        public int ConsecutiveFailures;
        public int RateLimitStreak;
        public bool HalfOpenProbeInFlight;
        public DateTimeOffset? LastSuccessUtc;
        public DateTimeOffset? LastFailureUtc;
        public DateTimeOffset CircuitOpenUntilUtc = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAfterUtc = DateTimeOffset.MinValue;
        public CircuitState CircuitState = CircuitState.Closed;
    }

    private readonly ConcurrentDictionary<string, ProviderState> _states = new(StringComparer.Ordinal);

    public bool CanExecute(string provider, out TimeSpan retryAfter)
    {
        var state = _states.GetOrAdd(provider, _ => new ProviderState());
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            var nextAllowed = state.CircuitOpenUntilUtc > state.RetryAfterUtc
                ? state.CircuitOpenUntilUtc
                : state.RetryAfterUtc;

            if (state.CircuitState == CircuitState.Open)
            {
                if (nextAllowed > now)
                {
                    retryAfter = nextAllowed - now;
                    return false;
                }

                state.CircuitState = CircuitState.HalfOpen;
                state.HalfOpenProbeInFlight = false;
            }

            if (state.CircuitState == CircuitState.HalfOpen)
            {
                if (state.HalfOpenProbeInFlight)
                {
                    retryAfter = TimeSpan.FromMilliseconds(500);
                    return false;
                }

                state.HalfOpenProbeInFlight = true;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (nextAllowed > now)
            {
                retryAfter = nextAllowed - now;
                return false;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void RecordSuccess(string provider, long latencyMs)
    {
        var state = _states.GetOrAdd(provider, _ => new ProviderState());
        lock (state)
        {
            state.TotalCalls++;
            state.SuccessCalls++;
            state.TotalLatencyMs += Math.Max(0, latencyMs);
            state.ConsecutiveFailures = 0;
            state.RateLimitStreak = 0;
            state.LastSuccessUtc = DateTimeOffset.UtcNow;
            state.RetryAfterUtc = DateTimeOffset.MinValue;
            state.CircuitOpenUntilUtc = DateTimeOffset.MinValue;
            state.HalfOpenProbeInFlight = false;
            state.CircuitState = CircuitState.Closed;
        }
    }

    public void RecordFailure(
        string provider,
        long latencyMs,
        bool isRateLimited,
        int failureThreshold,
        TimeSpan cooldown,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff)
    {
        var state = _states.GetOrAdd(provider, _ => new ProviderState());
        lock (state)
        {
            state.TotalCalls++;
            state.FailureCalls++;
            state.TotalLatencyMs += Math.Max(0, latencyMs);
            state.ConsecutiveFailures++;
            state.LastFailureUtc = DateTimeOffset.UtcNow;

            if (isRateLimited)
            {
                state.RateLimitHits++;
                state.RateLimitStreak = Math.Min(state.RateLimitStreak + 1, 6);
                var rawMs = baseBackoff.TotalMilliseconds * Math.Pow(2, state.RateLimitStreak - 1);
                var jitterMs = Random.Shared.Next(100, 700);
                var backoffMs = Math.Min(rawMs + jitterMs, maxBackoff.TotalMilliseconds);
                state.RetryAfterUtc = DateTimeOffset.UtcNow.AddMilliseconds(backoffMs);
            }
            else
            {
                state.RateLimitStreak = 0;
            }

            var shouldOpenFromThreshold = state.ConsecutiveFailures >= failureThreshold;
            var halfOpenProbeFailed = state.CircuitState == CircuitState.HalfOpen && state.HalfOpenProbeInFlight;
            if (shouldOpenFromThreshold || halfOpenProbeFailed)
            {
                state.CircuitState = CircuitState.Open;
                state.CircuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(cooldown);
                state.ConsecutiveFailures = 0;
            }

            state.HalfOpenProbeInFlight = false;
        }
    }

    public IReadOnlyDictionary<string, ProviderHealthSnapshot> GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var output = new Dictionary<string, ProviderHealthSnapshot>(StringComparer.Ordinal);
        foreach (var pair in _states)
        {
            var state = pair.Value;
            lock (state)
            {
                var total = state.TotalCalls;
                var successRate = total > 0 ? (state.SuccessCalls * 100d) / total : 100d;
                var failureRate = total > 0 ? (state.FailureCalls * 100d) / total : 0d;
                var avgLatency = total > 0 ? state.TotalLatencyMs / (double)total : 0d;
                var nextAllowed = state.CircuitOpenUntilUtc > state.RetryAfterUtc
                    ? state.CircuitOpenUntilUtc
                    : state.RetryAfterUtc;
                var circuitOpen = (state.CircuitState == CircuitState.Open && state.CircuitOpenUntilUtc > now)
                    || state.RetryAfterUtc > now;

                output[pair.Key] = new ProviderHealthSnapshot
                {
                    TotalCalls = total,
                    SuccessCalls = state.SuccessCalls,
                    FailureCalls = state.FailureCalls,
                    RateLimitHits = state.RateLimitHits,
                    SuccessRatePercent = Math.Round(successRate, 2),
                    FailureRatePercent = Math.Round(failureRate, 2),
                    AverageLatencyMs = Math.Round(avgLatency, 2),
                    LastSuccessUtc = state.LastSuccessUtc,
                    LastFailureUtc = state.LastFailureUtc,
                    CircuitState = state.CircuitState.ToString().ToUpperInvariant(),
                    CircuitOpen = circuitOpen,
                    CircuitOpenUntilUtc = circuitOpen ? nextAllowed : null
                };
            }
        }

        return output;
    }
}

public sealed class ProviderHealthSnapshot
{
    public long TotalCalls { get; set; }
    public long SuccessCalls { get; set; }
    public long FailureCalls { get; set; }
    public long RateLimitHits { get; set; }
    public double SuccessRatePercent { get; set; }
    public double FailureRatePercent { get; set; }
    public double AverageLatencyMs { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? LastFailureUtc { get; set; }
    public string CircuitState { get; set; } = "CLOSED";
    public bool CircuitOpen { get; set; }
    public DateTimeOffset? CircuitOpenUntilUtc { get; set; }
}
