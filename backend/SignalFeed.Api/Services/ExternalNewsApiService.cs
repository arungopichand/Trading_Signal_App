using System.Text.Json;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class ExternalNewsApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalNewsApiService> _logger;
    private int _missingKeyWarningLogged;

    public ExternalNewsApiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ExternalNewsApiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<NewsApiArticle?> GetLatestArticleAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var articles = await GetLatestArticlesAsync(symbol, 5, cancellationToken);
        return articles.FirstOrDefault();
    }

    public async Task<IReadOnlyList<NewsApiArticle>> GetLatestArticlesAsync(
        string symbol,
        int pageSize = 5,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["NEWSAPI__APIKEY"] ?? _configuration["NewsApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("NEWSAPI__APIKEY missing. Continuing with Finnhub news fallback.");
            }

            return [];
        }

        var boundedPageSize = Math.Clamp(pageSize, 1, 20);
        var q = Uri.EscapeDataString($"{symbol} stock");
        var requestUri = $"v2/everything?q={q}&language=en&sortBy=publishedAt&pageSize={boundedPageSize}&apiKey={Uri.EscapeDataString(apiKey)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NewsAPI request failed for {Symbol} with status code {StatusCode}. Falling back to Finnhub news.",
                    symbol.Trim().ToUpperInvariant(),
                    (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<NewsApiResponse>(stream, JsonOptions, cancellationToken);
            if (payload is null || !string.Equals(payload.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "NewsAPI returned a non-ok payload for {Symbol}. Status={Status} Code={Code}. Falling back to Finnhub news.",
                    symbol.Trim().ToUpperInvariant(),
                    payload?.Status ?? "null",
                    payload?.Code ?? "null");
                return [];
            }

            return payload.Articles
                .Where(article => !string.IsNullOrWhiteSpace(article.Title) && !string.IsNullOrWhiteSpace(article.Url))
                .OrderByDescending(article => article.PublishedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "NewsAPI fetch failed for {Symbol}. Falling back to Finnhub news.", symbol.Trim().ToUpperInvariant());
            return [];
        }
    }
}
