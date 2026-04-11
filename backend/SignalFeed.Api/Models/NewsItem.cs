namespace SignalFeed.Api.Models;

public sealed class NewsItem
{
    public string Headline { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public long Datetime { get; set; }

    public string Related { get; set; } = string.Empty;
}
