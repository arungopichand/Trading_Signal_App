using System.Text.Json;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class FinnhubService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<FinnhubService> _logger;

    public FinnhubService(
        HttpClient httpClient,
        IConfiguration config,
        ILogger<FinnhubService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<List<NewsItem>> GetNewsAsync()
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return [];
        }

        try
        {
            using var response = await _httpClient.GetAsync($"news?category=general&token={apiKey}");

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Finnhub news request hit the rate limit. Returning an empty news set.");
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Finnhub news request failed with status code {StatusCode}.",
                    response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var data = await JsonSerializer.DeserializeAsync<List<NewsItem>>(stream, JsonOptions);
            return data ?? [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error while retrieving Finnhub news.");
            return [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse error while retrieving Finnhub news.");
            return [];
        }
    }

    public async Task<QuoteResponse?> GetQuoteAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            _logger.LogWarning("Quote request skipped because the symbol was empty.");
            return null;
        }

        if (!TryGetApiKey(out var apiKey))
        {
            return null;
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        try
        {
            using var response = await _httpClient.GetAsync($"quote?symbol={encodedSymbol}&token={apiKey}");

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Finnhub quote request hit the rate limit for {Symbol}. Returning null.",
                    encodedSymbol);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Finnhub quote request failed for {Symbol} with status code {StatusCode}.",
                    encodedSymbol,
                    response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<QuoteResponse>(stream, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error while retrieving quote for {Symbol}.", encodedSymbol);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse error while retrieving quote for {Symbol}.", encodedSymbol);
            return null;
        }
    }

    private bool TryGetApiKey(out string apiKey)
    {
        apiKey = _config["FINNHUB__APIKEY"] ?? _config["Finnhub:ApiKey"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("Finnhub API key is not configured.");
            return false;
        }

        return true;
    }
}
