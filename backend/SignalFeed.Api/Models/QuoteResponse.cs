using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public class QuoteResponse
{
    [JsonPropertyName("c")]
    public decimal CurrentPrice { get; set; }

    [JsonPropertyName("pc")]
    public decimal PreviousClose { get; set; }

    [JsonPropertyName("h")]
    public decimal High { get; set; }

    [JsonPropertyName("l")]
    public decimal Low { get; set; }

    [JsonPropertyName("v")]
    public decimal Volume { get; set; }

    [JsonPropertyName("o")]
    public decimal OpenPrice { get; set; }

    [JsonPropertyName("t")]
    public long Timestamp { get; set; }
}
