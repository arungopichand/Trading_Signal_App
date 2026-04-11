using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public class CandleResponse
{
    [JsonPropertyName("s")]
    public string? Status { get; set; }

    [JsonPropertyName("o")]
    public List<decimal>? Open { get; set; }

    [JsonPropertyName("c")]
    public List<decimal>? Close { get; set; }

    [JsonPropertyName("v")]
    public List<decimal>? Volume { get; set; }
}
