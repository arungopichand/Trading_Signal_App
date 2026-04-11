namespace SignalFeed.Api.Models;

public class ScanBatchResult
{
    public IReadOnlyList<QuoteSnapshot> Snapshots { get; init; } = [];

    public IReadOnlyList<StockSignal> Signals { get; init; } = [];
}
