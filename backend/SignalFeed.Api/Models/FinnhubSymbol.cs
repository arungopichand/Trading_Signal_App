using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public sealed class FinnhubSymbol
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("displaySymbol")]
    public string DisplaySymbol { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
