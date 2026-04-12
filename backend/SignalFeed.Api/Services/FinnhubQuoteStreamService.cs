using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FinnhubQuoteStreamService : BackgroundService, IFinnhubWebSocketService
{
    private static readonly Uri BaseEndpoint = new("wss://ws.finnhub.io");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IOptionsMonitor<FinnhubWebSocketOptions> _optionsMonitor;
    private readonly ILogger<FinnhubQuoteStreamService> _logger;
    private readonly ConcurrentDictionary<string, byte> _subscribedSymbols = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouchedBySymbol = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamPriceSnapshot> _latestPriceBySymbol = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _socketGate = new();
    private ClientWebSocket? _socket;
    private volatile bool _isConnected;
    private long _reconnectCount;
    private DateTimeOffset _rateLimitedUntilUtc = DateTimeOffset.MinValue;

    public FinnhubQuoteStreamService(
        IOptionsMonitor<FinnhubWebSocketOptions> optionsMonitor,
        ILogger<FinnhubQuoteStreamService> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    public async Task SubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSymbol(symbol);
        if (normalized.Length == 0)
        {
            return;
        }

        _lastTouchedBySymbol[normalized] = DateTimeOffset.UtcNow;

        var options = _optionsMonitor.CurrentValue;
        var added = _subscribedSymbols.TryAdd(normalized, 0);
        if (!added)
        {
            return;
        }

        if (_subscribedSymbols.Count > Math.Max(1, options.MaxSubscribedSymbols))
        {
            _subscribedSymbols.TryRemove(normalized, out _);
            _lastTouchedBySymbol.TryRemove(normalized, out _);
            _logger.LogWarning(
                "Finnhub websocket symbol limit reached ({MaxSubscribedSymbols}). Symbol {Symbol} rejected.",
                options.MaxSubscribedSymbols,
                normalized);
            return;
        }

        if (_isConnected)
        {
            await SendSubscriptionMessageAsync("subscribe", normalized, cancellationToken);
        }
    }

    public async Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSymbol(symbol);
        if (normalized.Length == 0)
        {
            return;
        }

        _subscribedSymbols.TryRemove(normalized, out _);
        _lastTouchedBySymbol.TryRemove(normalized, out _);
        _latestPriceBySymbol.TryRemove(normalized, out _);

        if (_isConnected)
        {
            await SendSubscriptionMessageAsync("unsubscribe", normalized, cancellationToken);
        }
    }

    public IReadOnlyCollection<string> GetSubscribedSymbols()
    {
        return _subscribedSymbols.Keys.ToArray();
    }

    public bool TryGetFreshPrice(string symbol, out StreamPriceSnapshot snapshot)
    {
        snapshot = default!;
        var normalized = NormalizeSymbol(symbol);
        if (normalized.Length == 0 || !_latestPriceBySymbol.TryGetValue(normalized, out var found))
        {
            return false;
        }

        _lastTouchedBySymbol[normalized] = DateTimeOffset.UtcNow;

        var staleAfter = TimeSpan.FromSeconds(Math.Max(1, _optionsMonitor.CurrentValue.StaleAfterSeconds));
        if (DateTimeOffset.UtcNow - found.ReceivedTimestampUtc > staleAfter)
        {
            return false;
        }

        snapshot = found;
        return true;
    }

    public bool TryGetAnyPrice(string symbol, out StreamPriceSnapshot snapshot)
    {
        snapshot = new StreamPriceSnapshot();
        var normalized = NormalizeSymbol(symbol);
        if (normalized.Length == 0)
        {
            return false;
        }

        _lastTouchedBySymbol[normalized] = DateTimeOffset.UtcNow;
        if (_latestPriceBySymbol.TryGetValue(normalized, out var found))
        {
            snapshot = found;
            return true;
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            var token = ResolveToken(options);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Finnhub websocket is disabled because API key is missing.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            var nextDelayMs = Math.Max(250, options.InitialReconnectDelayMs);
            var maxDelayMs = Math.Max(nextDelayMs, options.MaxReconnectDelayMs);
            Interlocked.Increment(ref _reconnectCount);

            while (!stoppingToken.IsCancellationRequested)
            {
                ClientWebSocket? localSocket = null;
                var skipBackoffWait = false;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_rateLimitedUntilUtc > now)
                    {
                        var waitFor = _rateLimitedUntilUtc - now;
                        _logger.LogWarning(
                            "Finnhub websocket reconnect paused by rate-limit cooldown for {Seconds}s.",
                            Math.Ceiling(waitFor.TotalSeconds));
                        await Task.Delay(waitFor, stoppingToken);
                        continue;
                    }

                    localSocket = new ClientWebSocket();
                    localSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await localSocket.ConnectAsync(new Uri($"{BaseEndpoint}?token={token}"), stoppingToken);

                    lock (_socketGate)
                    {
                        _socket = localSocket;
                    }

                    _isConnected = true;
                    _logger.LogInformation("Finnhub websocket connected.");
                    await ResubscribeAllAsync(stoppingToken);

                    nextDelayMs = Math.Max(250, options.InitialReconnectDelayMs);
                    await ReceiveLoopAsync(localSocket, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (WebSocketException ex) when (IsRateLimitHandshake(ex))
                {
                    var rateLimitCooldownSeconds = Math.Max(30, options.RateLimitCooldownSeconds);
                    _rateLimitedUntilUtc = DateTimeOffset.UtcNow.AddSeconds(rateLimitCooldownSeconds);
                    skipBackoffWait = true;
                    _logger.LogWarning(
                        ex,
                        "Finnhub websocket handshake rate-limited (429). Cooling down reconnects for {Seconds}s.",
                        rateLimitCooldownSeconds);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Finnhub websocket session failed.");
                }
                finally
                {
                    _isConnected = false;
                    lock (_socketGate)
                    {
                        _socket = null;
                    }

                    if (localSocket is not null)
                    {
                        localSocket.Dispose();
                    }
                }

                if (skipBackoffWait)
                {
                    continue;
                }

                Interlocked.Increment(ref _reconnectCount);
                var jitterMs = Random.Shared.Next(100, 700);
                var waitMs = Math.Min(maxDelayMs, nextDelayMs) + jitterMs;
                _logger.LogWarning("Finnhub websocket reconnecting in {DelayMs}ms.", waitMs);
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), stoppingToken);
                nextDelayMs = Math.Min(maxDelayMs, nextDelayMs * 2);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _isConnected = false;
        ClientWebSocket? socket;
        lock (_socketGate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopping", cancellationToken);
                }
            }
            catch
            {
                // best effort shutdown
            }
            finally
            {
                socket.Dispose();
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        var nextCleanupAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _optionsMonitor.CurrentValue.CleanupIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextMessageAsync(socket, buffer, cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                ProcessMessage(payload);
            }

            if (DateTimeOffset.UtcNow >= nextCleanupAt)
            {
                await CleanupStaleSymbolsAsync(cancellationToken);
                nextCleanupAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _optionsMonitor.CurrentValue.CleanupIntervalSeconds));
            }
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await socket.ReceiveAsync(segment, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                if (result.EndOfMessage)
                {
                    return null;
                }

                continue;
            }

            if (result.Count > 0)
            {
                stream.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private void ProcessMessage(string payload)
    {
        FinnhubTradeMessage? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FinnhubTradeMessage>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Finnhub websocket parse failure.");
            return;
        }

        if (envelope is null)
        {
            return;
        }

        if (!string.Equals(envelope.Type, "trade", StringComparison.OrdinalIgnoreCase) || envelope.Data is null)
        {
            return;
        }

        var receivedAt = DateTimeOffset.UtcNow;
        foreach (var tick in envelope.Data)
        {
            if (tick.Price <= 0 || string.IsNullOrWhiteSpace(tick.Symbol))
            {
                continue;
            }

            var symbol = NormalizeSymbol(tick.Symbol);
            if (!_subscribedSymbols.ContainsKey(symbol))
            {
                continue;
            }

            var tradeAt = tick.TradeTimestampUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(tick.TradeTimestampUnixMs)
                : receivedAt;

            _latestPriceBySymbol[symbol] = new StreamPriceSnapshot
            {
                Symbol = symbol,
                Price = tick.Price,
                Volume = Math.Max(0, tick.Volume),
                TradeTimestampUtc = tradeAt,
                ReceivedTimestampUtc = receivedAt,
                SourceProvider = "WebSocket"
            };
        }
    }

    private async Task ResubscribeAllAsync(CancellationToken cancellationToken)
    {
        foreach (var symbol in _subscribedSymbols.Keys.OrderBy(static s => s, StringComparer.Ordinal))
        {
            await SendSubscriptionMessageAsync("subscribe", symbol, cancellationToken);
        }
    }

    private async Task CleanupStaleSymbolsAsync(CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(30, _optionsMonitor.CurrentValue.SymbolTtlSeconds));
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _lastTouchedBySymbol.ToArray())
        {
            if (now - kvp.Value <= ttl)
            {
                continue;
            }

            var symbol = kvp.Key;
            if (_subscribedSymbols.TryRemove(symbol, out _))
            {
                _lastTouchedBySymbol.TryRemove(symbol, out _);
                _latestPriceBySymbol.TryRemove(symbol, out _);
                await SendSubscriptionMessageAsync("unsubscribe", symbol, cancellationToken);
            }
        }
    }

    private async Task SendSubscriptionMessageAsync(string type, string symbol, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        lock (_socketGate)
        {
            socket = _socket;
        }

        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { type, symbol });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static string ResolveToken(FinnhubWebSocketOptions options)
    {
        return Environment.GetEnvironmentVariable("FINNHUB__APIKEY")
            ?? options.Token
            ?? string.Empty;
    }

    private static string NormalizeSymbol(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();
    }

    private static bool IsRateLimitHandshake(WebSocketException ex)
    {
        return ex.Message.Contains("429", StringComparison.Ordinal);
    }
}
