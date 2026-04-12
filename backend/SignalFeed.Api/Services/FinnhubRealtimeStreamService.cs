using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FinnhubRealtimeStreamService : BackgroundService
{
    private static readonly Uri WsEndpoint = new("wss://ws.finnhub.io");
    private static readonly TimeSpan SymbolEmitCooldown = TimeSpan.FromMilliseconds(750);
    private readonly FinnhubService _finnhubService;
    private readonly FeedService _feedService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly ILogger<FinnhubRealtimeStreamService> _logger;
    private readonly Dictionary<string, decimal> _lastTradeBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastEmitBySymbol = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribed = [];
    private int _subscriptionOffset;

    public FinnhubRealtimeStreamService(
        FinnhubService finnhubService,
        FeedService feedService,
        SymbolUniverseService symbolUniverseService,
        ILogger<FinnhubRealtimeStreamService> logger)
    {
        _finnhubService = finnhubService;
        _feedService = feedService;
        _symbolUniverseService = symbolUniverseService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSocketSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Finnhub realtime stream service is stopping.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Finnhub websocket disconnected. Reconnecting in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunSocketSessionAsync(CancellationToken cancellationToken)
    {
        var apiKey = _finnhubService.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Finnhub websocket skipped because API key is missing.");
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            return;
        }

        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(new Uri($"{WsEndpoint}?token={apiKey}"), cancellationToken);
        _logger.LogInformation("Finnhub websocket connected to {Endpoint}.", WsEndpoint);
        _subscribed.Clear();
        await UpdateSubscriptionsAsync(socket, cancellationToken);

        var receiveBuffer = new byte[32_768];
        var subscriptionRefreshAt = DateTimeOffset.UtcNow.AddMinutes(2);

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, receiveBuffer, cancellationToken);
            if (!string.IsNullOrWhiteSpace(message))
            {
                _logger.LogDebug("Finnhub websocket message received.");
                await ProcessSocketMessageAsync(message, cancellationToken);
            }

            if (DateTimeOffset.UtcNow >= subscriptionRefreshAt)
            {
                await UpdateSubscriptionsAsync(socket, cancellationToken);
                subscriptionRefreshAt = DateTimeOffset.UtcNow.AddMinutes(2);
            }
        }
    }

    private async Task UpdateSubscriptionsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var universe = await _symbolUniverseService.GetUniverseAsync(cancellationToken);
        var safeOffset = universe.Count == 0 ? 0 : _subscriptionOffset % universe.Count;
        var rotatingSlice = await _symbolUniverseService.GetTopUniverseSliceAsync(50, safeOffset, cancellationToken);
        _subscriptionOffset += 50;

        var active = _feedService.GetHotSymbols(25);
        var targets = rotatingSlice
            .Concat(active)
            .Distinct(StringComparer.Ordinal)
            .Take(60)
            .ToList();

        foreach (var symbol in _subscribed.Except(targets).ToList())
        {
            await SendSubscriptionAsync(socket, "unsubscribe", symbol, cancellationToken);
            _subscribed.Remove(symbol);
        }

        foreach (var symbol in targets.Where(symbol => !_subscribed.Contains(symbol)))
        {
            await SendSubscriptionAsync(socket, "subscribe", symbol, cancellationToken);
            _subscribed.Add(symbol);
        }
    }

    private static async Task SendSubscriptionAsync(
        ClientWebSocket socket,
        string type,
        string symbol,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { type, symbol });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task ProcessSocketMessageAsync(string message, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<TradeEnvelope>(message);
        if (envelope is null || !string.Equals(envelope.Type, "trade", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tradeData = envelope.Data;
        if (tradeData is null)
        {
            return;
        }

        foreach (var trade in tradeData)
        {
            if (string.IsNullOrWhiteSpace(trade.Symbol) || trade.Price <= 0)
            {
                continue;
            }

            var symbol = trade.Symbol.Trim().ToUpperInvariant();
            var prior = _lastTradeBySymbol.GetValueOrDefault(symbol, trade.Price);
            _lastTradeBySymbol[symbol] = trade.Price;
            var change = prior == 0 ? 0 : ((trade.Price - prior) / prior) * 100m;
            var volume = trade.Volume;
            if (Math.Abs(change) < 0.5m && volume < 2_000m)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastEmitBySymbol.TryGetValue(symbol, out var lastEmitAt) && now - lastEmitAt < SymbolEmitCooldown)
            {
                continue;
            }

            var type = change >= 2m ? "SPIKE" : change > 0 ? "BULLISH" : "BEARISH";
            var score = Math.Round(Math.Abs(change) * 15m + Math.Min(40m, volume / 500m), 2);
            if (score < 60m)
            {
                continue;
            }
            _lastEmitBySymbol[symbol] = now;

            var item = new FeedItem
            {
                Symbol = symbol,
                CountryCode = "US",
                Price = Math.Round(trade.Price, 4),
                ChangePercent = Math.Round(change, 2),
                SignalType = type,
                Score = score,
                ActivityScore = score,
                Confidence = score > 100m ? "HIGH" : score > 70m ? "MEDIUM" : "LOW",
                TradeReadiness = "WATCH",
                VolumeRatio = null,
                Momentum = Math.Round(change, 2),
                Sentiment = "NEUTRAL",
                Acceleration = null,
                GapPercent = null,
                NewsCategory = string.Empty,
                RepeatCount = 1,
                MomentumDetectedAt = now,
                Headline = $"Tape move {change:+0.##;-0.##;0}% on {volume:0} shares.",
                Reason = $"Realtime tape momentum + volume burst ({volume:0} shares)",
                Timestamp = now,
                Source = "TAPE"
            };

            await _feedService.AddItemAsync(item, cancellationToken);
        }
    }

    private static async Task<string?> ReceiveMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var segment = new ArraySegment<byte>(buffer);
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(segment, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class TradeEnvelope
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("data")]
        public List<TradeTick>? Data { get; set; }
    }

    private sealed class TradeTick
    {
        [JsonPropertyName("s")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("p")]
        public decimal Price { get; set; }

        [JsonPropertyName("v")]
        public decimal Volume { get; set; }
    }
}
