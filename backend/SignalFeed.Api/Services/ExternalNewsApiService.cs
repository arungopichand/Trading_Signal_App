using System.Text.Json;
using System.Net;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class ExternalNewsApiService
{
    private static readonly TimeSpan GeneralFailureCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthFailureCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(2);
    private const int ConsecutiveFailureThreshold = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalNewsApiService> _logger;
    private readonly object _stateGate = new();
    private int _consecutiveFailures;
    private DateTimeOffset _disabledUntil = DateTimeOffset.MinValue;
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
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (IsTemporarilyDisabled(out var disabledFor))
        {
            _logger.LogInformation(
                "NewsAPI temporarily disabled for {Seconds}s. Skipping fetch for {Symbol}.",
                Math.Ceiling(disabledFor.TotalSeconds),
                normalizedSymbol);
            return [];
        }

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
        var q = Uri.EscapeDataString($"{normalizedSymbol} stock");
        var requestUri = $"v2/everything?q={q}&language=en&sortBy=publishedAt&pageSize={boundedPageSize}&apiKey={Uri.EscapeDataString(apiKey)}";

        return await ExecuteRequestAsync(requestUri, normalizedSymbol, cancellationToken);
    }

    public async Task<IReadOnlyList<NewsApiArticle>> GetGlobalMarketArticlesAsync(
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (IsTemporarilyDisabled(out var disabledFor))
        {
            _logger.LogInformation(
                "NewsAPI temporarily disabled for {Seconds}s. Skipping global fetch.",
                Math.Ceiling(disabledFor.TotalSeconds));
            return [];
        }

        var apiKey = _configuration["NEWSAPI__APIKEY"] ?? _configuration["NewsApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("NEWSAPI__APIKEY missing. Global news will be skipped.");
            }

            return [];
        }

        var boundedPageSize = Math.Clamp(pageSize, 5, 30);
        var q = Uri.EscapeDataString("stock market OR equities OR wall street");
        var requestUri = $"v2/everything?q={q}&language=en&sortBy=publishedAt&pageSize={boundedPageSize}&apiKey={Uri.EscapeDataString(apiKey)}";

        return await ExecuteRequestAsync(requestUri, "GLOBAL", cancellationToken);
    }

    private async Task<IReadOnlyList<NewsApiArticle>> ExecuteRequestAsync(
        string requestUri,
        string context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                RegisterFailure(response.StatusCode, context);
                _logger.LogWarning(
                    "NewsAPI request failed for {Context} with status code {StatusCode}.",
                    context,
                    (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<NewsApiResponse>(stream, JsonOptions, cancellationToken);
            if (payload is null || !string.Equals(payload.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                RegisterFailure(null, context);
                _logger.LogWarning(
                    "NewsAPI returned a non-ok payload for {Context}. Status={Status} Code={Code}.",
                    context,
                    payload?.Status ?? "null",
                    payload?.Code ?? "null");
                return [];
            }

            RegisterSuccess();
            return payload.Articles
                .Where(article => !string.IsNullOrWhiteSpace(article.Title) && !string.IsNullOrWhiteSpace(article.Url))
                .OrderByDescending(article => article.PublishedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            RegisterFailure(null, context);
            _logger.LogWarning(ex, "NewsAPI fetch failed for {Context}.", context);
            return [];
        }
    }

    private bool IsTemporarilyDisabled(out TimeSpan remaining)
    {
        lock (_stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_disabledUntil > now)
            {
                remaining = _disabledUntil - now;
                return true;
            }

            remaining = TimeSpan.Zero;
            return false;
        }
    }

    private void RegisterSuccess()
    {
        lock (_stateGate)
        {
            _consecutiveFailures = 0;
        }
    }

    private void RegisterFailure(HttpStatusCode? statusCode, string symbol)
    {
        lock (_stateGate)
        {
            _consecutiveFailures++;
            var now = DateTimeOffset.UtcNow;

            if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _disabledUntil = now.Add(AuthFailureCooldown);
                _consecutiveFailures = 0;
                _logger.LogWarning(
                    "NewsAPI unauthorized/forbidden for {Symbol}. Cooling down for {Minutes}m.",
                    symbol,
                    AuthFailureCooldown.TotalMinutes);
                return;
            }

            if (statusCode == (HttpStatusCode)429)
            {
                _disabledUntil = now.Add(RateLimitCooldown);
                _consecutiveFailures = 0;
                _logger.LogWarning(
                    "NewsAPI rate-limited for {Symbol}. Cooling down for {Minutes}m.",
                    symbol,
                    RateLimitCooldown.TotalMinutes);
                return;
            }

            if (_consecutiveFailures >= ConsecutiveFailureThreshold)
            {
                _disabledUntil = now.Add(GeneralFailureCooldown);
                _consecutiveFailures = 0;
                _logger.LogWarning(
                    "NewsAPI repeated failures reached threshold. Cooling down for {Minutes}m.",
                    GeneralFailureCooldown.TotalMinutes);
            }
        }
    }
}
