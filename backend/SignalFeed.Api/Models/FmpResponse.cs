using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public sealed class FmpProfileItem
{
    [JsonPropertyName("mktCap")]
    public decimal? MarketCap { get; set; }
}

public sealed class FmpFloatSharesItem
{
    [JsonPropertyName("floatShares")]
    public decimal? FloatShares { get; set; }
}

public sealed class FmpInstitutionalOwnershipItem
{
    [JsonPropertyName("ownershipPercent")]
    public decimal? OwnershipPercent { get; set; }
}

public sealed class FmpFactors
{
    public decimal? MarketCap { get; init; }

    public decimal? FloatShares { get; init; }

    public decimal? InstitutionalOwnership { get; init; }
}
