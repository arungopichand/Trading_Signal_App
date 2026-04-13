namespace SignalFeed.Api.Models;

public sealed class SimulationPerformanceSnapshot
{
    public int TradeCount { get; set; }

    public int WinCount { get; set; }

    public decimal WinRatePercent { get; set; }

    public decimal NetPnl { get; set; }

    public decimal ExpectancyPerTrade { get; set; }

    public decimal CurrentEquity { get; set; }

    public decimal PeakEquity { get; set; }

    public decimal CurrentDrawdownPercent { get; set; }

    public decimal MaxDrawdownPercent { get; set; }
}
