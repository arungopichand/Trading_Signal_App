using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public sealed class PolygonAggregateResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("results")]
    public List<PolygonAggregateBar> Results { get; set; } = [];
}

public sealed class PolygonAggregateBar
{
    [JsonPropertyName("o")]
    public decimal Open { get; set; }

    [JsonPropertyName("h")]
    public decimal High { get; set; }

    [JsonPropertyName("l")]
    public decimal Low { get; set; }

    [JsonPropertyName("c")]
    public decimal Close { get; set; }

    [JsonPropertyName("v")]
    public decimal Volume { get; set; }

    [JsonPropertyName("t")]
    public long Timestamp { get; set; }
}

public sealed class PolygonSnapshotResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("ticker")]
    public PolygonSnapshotTicker? Ticker { get; set; }
}

public sealed class PolygonSnapshotTicker
{
    [JsonPropertyName("lastTrade")]
    public PolygonLastTrade? LastTrade { get; set; }

    [JsonPropertyName("day")]
    public PolygonSnapshotDay? Day { get; set; }

    [JsonPropertyName("prevDay")]
    public PolygonSnapshotPrevDay? PrevDay { get; set; }

    [JsonPropertyName("todaysChangePerc")]
    public decimal? TodaysChangePercent { get; set; }

    [JsonPropertyName("updated")]
    public long? UpdatedUnixMs { get; set; }
}

public sealed class PolygonLastTrade
{
    [JsonPropertyName("p")]
    public decimal Price { get; set; }
}

public sealed class PolygonSnapshotDay
{
    [JsonPropertyName("o")]
    public decimal Open { get; set; }

    [JsonPropertyName("h")]
    public decimal High { get; set; }

    [JsonPropertyName("l")]
    public decimal Low { get; set; }

    [JsonPropertyName("c")]
    public decimal Close { get; set; }

    [JsonPropertyName("v")]
    public decimal Volume { get; set; }
}

public sealed class PolygonSnapshotPrevDay
{
    [JsonPropertyName("c")]
    public decimal Close { get; set; }
}
