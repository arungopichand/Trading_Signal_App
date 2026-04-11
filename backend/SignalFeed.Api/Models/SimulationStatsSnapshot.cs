namespace SignalFeed.Api.Models;

public sealed class SimulationStatsSnapshot
{
    public int EngineSignalsPerMinute { get; set; }

    public int FallbackSignalsPerMinute { get; set; }

    public int TotalSignalsPerMinute { get; set; }

    public decimal FallbackRatePercent { get; set; }
}
