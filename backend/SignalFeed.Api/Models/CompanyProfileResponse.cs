using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public class CompanyProfileResponse
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; set; }

    [JsonPropertyName("marketCapitalization")]
    public decimal? MarketCapitalization { get; set; }
}
