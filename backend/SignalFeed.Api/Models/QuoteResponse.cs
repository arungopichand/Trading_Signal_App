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
}
