using System.Collections.Concurrent;
using SignalFeed.Api.Models;
using SignalFeed.Api.Services;

namespace SignalFeed.Tests.TestDoubles;

internal sealed class FakeFinnhubWebSocketService : IFinnhubWebSocketService
{
    private readonly ConcurrentDictionary<string, StreamPriceSnapshot> _prices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _symbols = new(StringComparer.Ordinal);

    public long ReconnectCount { get; set; }

    public Task SubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            _symbols.TryAdd(symbol.Trim().ToUpperInvariant(), 0);
        }

        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            _symbols.TryRemove(symbol.Trim().ToUpperInvariant(), out _);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyCollection<string> GetSubscribedSymbols() => _symbols.Keys.ToArray();

    public bool TryGetFreshPrice(string symbol, out StreamPriceSnapshot snapshot)
    {
        snapshot = new StreamPriceSnapshot();
        var key = Normalize(symbol);
        if (!_prices.TryGetValue(key, out var found))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - found.ReceivedTimestampUtc > TimeSpan.FromSeconds(15))
        {
            return false;
        }

        snapshot = found;
        return true;
    }

    public bool TryGetAnyPrice(string symbol, out StreamPriceSnapshot snapshot)
    {
        snapshot = new StreamPriceSnapshot();
        if (_prices.TryGetValue(Normalize(symbol), out var found))
        {
            snapshot = found;
            return true;
        }

        return false;
    }

    public void SetPrice(
        string symbol,
        decimal price,
        decimal volume = 0,
        DateTimeOffset? tradeTimeUtc = null,
        DateTimeOffset? receivedAtUtc = null)
    {
        var key = Normalize(symbol);
        _prices[key] = new StreamPriceSnapshot
        {
            Symbol = key,
            Price = price,
            Volume = volume,
            TradeTimestampUtc = tradeTimeUtc ?? DateTimeOffset.UtcNow,
            ReceivedTimestampUtc = receivedAtUtc ?? DateTimeOffset.UtcNow,
            SourceProvider = "WebSocket"
        };
    }

    public void Clear(string symbol)
    {
        _prices.TryRemove(Normalize(symbol), out _);
    }

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();
}
