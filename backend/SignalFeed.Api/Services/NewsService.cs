using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class NewsService
{
    private static readonly string[] PositiveKeywords =
    [
        "surge", "beats", "strong", "partnership", "contract", "approval", "launch", "record", "growth", "profit"
    ];

    private static readonly string[] NegativeKeywords =
    [
        "offering", "dilution", "lawsuit", "investigation", "drop", "decline", "misses", "downgrade", "loss", "bankruptcy"
    ];

    private readonly FinnhubService _finnhubService;
    private readonly ILogger<NewsService> _logger;
    private readonly Dictionary<string, (DateTimeOffset FetchedAt, NormalizedNewsItem? Item)> _latestBySymbol = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenUrls = [];
    private readonly object _gate = new();
    private readonly TimeSpan _cacheWindow = TimeSpan.FromMinutes(2);

    public NewsService(FinnhubService finnhubService, ILogger<NewsService> logger)
    {
        _finnhubService = finnhubService;
        _logger = logger;
    }

    public async Task<NormalizedNewsItem?> GetLatestNewsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_latestBySymbol.TryGetValue(symbol, out var cached) &&
                DateTimeOffset.UtcNow - cached.FetchedAt < _cacheWindow)
            {
                return cached.Item;
            }
        }

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-2);
        var news = await _finnhubService.GetCompanyNewsAsync(symbol, from, to, cancellationToken);
        var latest = news
            .Where(item => !string.IsNullOrWhiteSpace(item.Headline) && !string.IsNullOrWhiteSpace(item.Url))
            .OrderByDescending(item => item.Datetime)
            .Select(item => Normalize(symbol, item))
            .FirstOrDefault();

        lock (_gate)
        {
            _latestBySymbol[symbol] = (DateTimeOffset.UtcNow, latest);
        }

        return latest;
    }

    public async Task<IReadOnlyList<NormalizedNewsItem>> PullFreshNewsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var output = new List<NormalizedNewsItem>();
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-1);

        foreach (var symbol in symbols.Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _finnhubService.GetCompanyNewsAsync(symbol, from, to, cancellationToken);
            var latest = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Headline) && !string.IsNullOrWhiteSpace(item.Url))
                .OrderByDescending(item => item.Datetime)
                .FirstOrDefault();

            if (latest is null)
            {
                continue;
            }

            var normalized = Normalize(symbol, latest);
            lock (_gate)
            {
                _latestBySymbol[symbol] = (DateTimeOffset.UtcNow, normalized);
                if (!_seenUrls.Add(normalized.Url))
                {
                    continue;
                }

                if (_seenUrls.Count > 2000)
                {
                    _seenUrls.Clear();
                    _seenUrls.Add(normalized.Url);
                }
            }

            output.Add(normalized);
        }

        if (output.Count > 0)
        {
            _logger.LogInformation("Pulled {Count} fresh news items.", output.Count);
        }

        return output;
    }

    private static NormalizedNewsItem Normalize(string symbol, NewsItem news)
    {
        var score = ScoreSentiment(news.Headline, news.Summary);
        return new NormalizedNewsItem
        {
            Symbol = symbol,
            Headline = news.Headline.Trim(),
            Summary = news.Summary?.Trim() ?? string.Empty,
            Source = string.IsNullOrWhiteSpace(news.Source) ? "NEWS" : news.Source.Trim(),
            Url = news.Url.Trim(),
            Datetime = DateTimeOffset.FromUnixTimeSeconds(Math.Max(news.Datetime, 0)),
            SentimentScore = score
        };
    }

    private static decimal ScoreSentiment(string headline, string summary)
    {
        var text = $"{headline} {summary}".ToLowerInvariant();
        var positiveHits = PositiveKeywords.Count(text.Contains);
        var negativeHits = NegativeKeywords.Count(text.Contains);
        var score = (positiveHits - negativeHits) / 3m;
        return Math.Clamp(score, -1m, 1m);
    }
}
