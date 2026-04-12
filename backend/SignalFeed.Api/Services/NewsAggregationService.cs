using Microsoft.Extensions.Caching.Memory;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class NewsAggregationService
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

    private readonly ExternalNewsApiService _newsApiService;
    private readonly NewsService _finnhubNewsService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NewsAggregationService> _logger;

    public NewsAggregationService(
        ExternalNewsApiService newsApiService,
        NewsService finnhubNewsService,
        IMemoryCache cache,
        ILogger<NewsAggregationService> logger)
    {
        _newsApiService = newsApiService;
        _finnhubNewsService = finnhubNewsService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<NormalizedNewsItem?> GetLatestNewsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var cacheKey = $"news:{normalizedSymbol}";
        if (_cache.TryGetValue<NormalizedNewsItem>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogInformation("Cache HIT {key}", cacheKey);
            return cached;
        }

        _logger.LogInformation("Cache MISS {key}", cacheKey);
        var newsApiArticles = await _newsApiService.GetLatestArticlesAsync(normalizedSymbol, 5, cancellationToken);
        var externalNews = newsApiArticles
            .Select(article => Normalize(normalizedSymbol, article))
            .Where(item => item.Datetime != default)
            .OrderByDescending(item => item.Datetime)
            .FirstOrDefault();

        if (externalNews is not null)
        {
            _logger.LogInformation("NewsAPI used for {Symbol}.", normalizedSymbol);
            _cache.Set(cacheKey, externalNews, TimeSpan.FromSeconds(60));
            return externalNews;
        }

        var finnhubNews = await _finnhubNewsService.GetLatestNewsAsync(normalizedSymbol, cancellationToken);
        if (finnhubNews is not null)
        {
            _logger.LogInformation("Finnhub news fallback used for {Symbol}.", normalizedSymbol);
            _cache.Set(cacheKey, finnhubNews, TimeSpan.FromSeconds(60));
        }

        return finnhubNews;
    }

    public async Task<IReadOnlyList<NormalizedNewsItem>> PullFreshNewsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var output = new List<NormalizedNewsItem>();
        var symbolSet = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToHashSet(StringComparer.Ordinal);

        if (symbolSet.Count == 0)
        {
            return output;
        }

        var globalArticles = await _newsApiService.GetGlobalMarketArticlesAsync(20, cancellationToken);
        foreach (var article in globalArticles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headline = article.Title?.Trim() ?? string.Empty;
            var summary = article.Description?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(headline) && string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            var text = $"{headline} {summary}".ToUpperInvariant();
            var matchedSymbol = symbolSet.FirstOrDefault(symbol => text.Contains(symbol, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(matchedSymbol))
            {
                continue;
            }

            var normalized = Normalize(matchedSymbol, article);
            output.Add(normalized);
            if (output.Count >= 8)
            {
                break;
            }
        }

        if (output.Count > 0)
        {
            _logger.LogInformation("Pulled {Count} aggregated news items.", output.Count);
        }

        return output;
    }

    private static NormalizedNewsItem Normalize(string symbol, NewsApiArticle article)
    {
        var headline = article.Title?.Trim() ?? string.Empty;
        var summary = article.Description?.Trim() ?? string.Empty;
        var score = ScoreSentiment(headline, summary);
        return new NormalizedNewsItem
        {
            Symbol = symbol.Trim().ToUpperInvariant(),
            Headline = headline,
            Summary = summary,
            Source = string.IsNullOrWhiteSpace(article.Source?.Name) ? "NEWSAPI" : article.Source!.Name!.Trim(),
            Url = article.Url?.Trim() ?? string.Empty,
            Datetime = article.PublishedAt ?? DateTimeOffset.UtcNow,
            SentimentScore = score,
            Sentiment = ResolveSentiment(score),
            Category = ResolveCategory(headline, summary)
        };
    }

    private static decimal ScoreSentiment(string headline, string summary)
    {
        var text = $"{headline} {summary}".ToLowerInvariant();
        var positiveHits = PositiveKeywords.Count(text.Contains);
        var negativeHits = NegativeKeywords.Count(text.Contains);
        return Math.Clamp((positiveHits - negativeHits) / 3m, -1m, 1m);
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
