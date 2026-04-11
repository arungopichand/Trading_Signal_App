using System.Text.Json.Serialization;

namespace SignalFeed.Api.Models;

public sealed class NewsApiResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("articles")]
    public List<NewsApiArticle> Articles { get; set; } = [];
}

public sealed class NewsApiArticle
{
    [JsonPropertyName("source")]
    public NewsApiSource? Source { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class NewsApiSource
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
