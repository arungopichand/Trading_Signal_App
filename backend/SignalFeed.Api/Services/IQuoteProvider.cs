using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public interface IQuoteProvider
{
    Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);
}
