using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public interface IFinnhubWebSocketService
{
    Task SubscribeAsync(string symbol, CancellationToken cancellationToken = default);
    Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default);
    IReadOnlyCollection<string> GetSubscribedSymbols();
    bool TryGetFreshPrice(string symbol, out StreamPriceSnapshot snapshot);
    bool TryGetAnyPrice(string symbol, out StreamPriceSnapshot snapshot);
    long ReconnectCount { get; }
}
