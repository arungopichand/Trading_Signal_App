using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public class BasicFinancialsResponse
{
    [JsonPropertyName("metric")]
    public FinancialMetric? Metric { get; set; }
}

public class FinancialMetric
{
    [JsonPropertyName("shareFloat")]
    public decimal? ShareFloat { get; set; }

    [JsonPropertyName("institutionOwnership")]
    public decimal? InstitutionOwnership { get; set; }
}
