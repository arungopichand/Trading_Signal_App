using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public interface IMarketDataService
{
    Task<UnifiedMarketData?> GetUnifiedMarketData(string symbol, CancellationToken cancellationToken = default);
}
