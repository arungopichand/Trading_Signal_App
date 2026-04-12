using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public interface IMarketDataService
{
    Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);

    Task<UnifiedMarketData> GetUnifiedMarketDataAsync(
        string symbol,
        bool includeNews,
        CancellationToken cancellationToken = default);

    Task<UnifiedMarketData?> GetUnifiedMarketData(string symbol, CancellationToken cancellationToken = default);

    void MarkTopOpportunity(string symbol);

    MarketDataHealthMetrics GetHealthMetrics();
}
