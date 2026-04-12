using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public sealed class FinnhubTradeMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("data")]
    public List<FinnhubTradeTick>? Data { get; set; }
}

public sealed class FinnhubTradeTick
{
    [JsonPropertyName("s")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("p")]
    public decimal Price { get; set; }

    [JsonPropertyName("v")]
    public decimal Volume { get; set; }

    [JsonPropertyName("t")]
    public long TradeTimestampUnixMs { get; set; }
}
