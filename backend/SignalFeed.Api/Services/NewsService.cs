using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class NewsService
{
    private static readonly string[] PositiveKeywords =
    [
        "surge", "beats", "beat", "strong", "partnership", "contract", "approval", "launch", "record", "growth",
        "profit", "raises guidance", "upgrade", "buyback", "wins contract", "fda approval"
    ];

    private static readonly string[] NegativeKeywords =
    [
        "offering", "dilution", "lawsuit", "investigation", "drop", "decline", "misses", "miss", "downgrade",
        "loss", "bankruptcy", "cuts guidance", "sec probe", "delay", "halt", "recall"
    ];

    private static readonly Dictionary<string, string[]> CategoryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["earnings"] = ["earnings", "eps", "guidance", "revenue", "profit", "forecast"],
        ["analyst"] = ["analyst", "downgrade", "upgrade", "price target", "rating"],
        ["product"] = ["launch", "product", "platform", "release", "approval", "patent"],
        ["acquisition"] = ["acquisition", "acquire", "merger", "buyout", "takeover"],
        ["legal"] = ["lawsuit", "legal", "investigation", "sec", "probe", "settlement"],
        ["contract"] = ["contract", "deal", "partnership", "agreement", "order", "award"],
        ["fda"] = ["fda", "phase", "trial", "clinical", "drug", "approval"],
        ["market commentary"] = ["market", "sector", "macro", "fed", "economy", "commentary"]
    };

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
        var sentiment = ResolveSentiment(score);
        var category = ResolveCategory(news.Headline, news.Summary);
        return new NormalizedNewsItem
        {
            Symbol = symbol,
            Headline = news.Headline.Trim(),
            Summary = news.Summary?.Trim() ?? string.Empty,
            Source = string.IsNullOrWhiteSpace(news.Source) ? "NEWS" : news.Source.Trim(),
            Url = news.Url.Trim(),
            Datetime = DateTimeOffset.FromUnixTimeSeconds(Math.Max(news.Datetime, 0)),
            SentimentScore = score,
            Sentiment = sentiment,
            Category = category
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

    private static string ResolveSentiment(decimal score)
    {
        if (score > 0.2m)
        {
            return "BULLISH";
        }

        if (score < -0.2m)
        {
            return "BEARISH";
        }

        return "NEUTRAL";
    }

    private static string ResolveCategory(string headline, string summary)
    {
        var text = $"{headline} {summary}".ToLowerInvariant();
        foreach (var pair in CategoryKeywords)
        {
            if (pair.Value.Any(text.Contains))
            {
                return pair.Key;
            }
        }

        return "market commentary";
    }
}
