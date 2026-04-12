using SignalFeed.Api.Services;

namespace SignalFeed.Tests;

public sealed class ProviderHealthTrackerTests
{
    [Fact]
    public async Task Opens_AfterThreshold_ThenHalfOpen_ThenClosesOnSuccess()
    {
        var tracker = new ProviderHealthTracker();
        const string provider = "FinnhubService";
        var cooldown = TimeSpan.FromMilliseconds(150);

        tracker.RecordFailure(provider, 10, false, 3, cooldown, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(200));
        tracker.RecordFailure(provider, 10, false, 3, cooldown, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(200));
        tracker.RecordFailure(provider, 10, false, 3, cooldown, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(200));

        Assert.False(tracker.CanExecute(provider, out _));

        await Task.Delay(cooldown + TimeSpan.FromMilliseconds(30));
        Assert.True(tracker.CanExecute(provider, out _)); // half-open probe

        tracker.RecordSuccess(provider, 12);
        Assert.True(tracker.CanExecute(provider, out _));
    }

    [Fact]
    public async Task HalfOpenProbeFailure_ReopensCircuit()
    {
        var tracker = new ProviderHealthTracker();
        const string provider = "PolygonService";
        var cooldown = TimeSpan.FromMilliseconds(100);

        tracker.RecordFailure(provider, 10, false, 1, cooldown, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80));
        Assert.False(tracker.CanExecute(provider, out _));

        await Task.Delay(cooldown + TimeSpan.FromMilliseconds(25));
        Assert.True(tracker.CanExecute(provider, out _)); // half-open probe

        tracker.RecordFailure(provider, 10, false, 1, cooldown, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80));
        Assert.False(tracker.CanExecute(provider, out _));
    }

    [Fact]
    public void RateLimitFailure_TracksRateLimitHits()
    {
        var tracker = new ProviderHealthTracker();
        const string provider = "NewsApi";

        tracker.RecordFailure(
            provider,
            latencyMs: 20,
            isRateLimited: true,
            failureThreshold: 3,
            cooldown: TimeSpan.FromSeconds(1),
            baseBackoff: TimeSpan.FromMilliseconds(50),
            maxBackoff: TimeSpan.FromMilliseconds(500));

        var snapshot = tracker.GetSnapshot();
        Assert.True(snapshot.TryGetValue(provider, out var providerSnapshot));
        Assert.NotNull(providerSnapshot);
        Assert.True(providerSnapshot!.RateLimitHits >= 1);
    }
}
