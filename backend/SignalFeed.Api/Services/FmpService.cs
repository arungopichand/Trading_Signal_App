using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FmpService : IQuoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FmpService> _logger;
    private readonly TimeSpan _cacheWindow = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _quoteCacheWindow = TimeSpan.FromSeconds(5);
    private int _missingKeyWarningLogged;

    public FmpService(HttpClient httpClient, IConfiguration configuration, IMemoryCache cache, ILogger<FmpService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    public async Task<FmpFactors?> GetFactorsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var cacheKey = $"fundamentals:{normalizedSymbol}";
        if (_cache.TryGetValue<FmpFactors>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogInformation("Cache HIT {key}", cacheKey);
            return cached;
        }

        _logger.LogInformation("Cache MISS {key}", cacheKey);
        var apiKey = _configuration["FMP__APIKEY"] ?? _configuration["Fmp:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("FMP__APIKEY missing. Continuing without Financial Modeling Prep factors.");
            }

            return null;
        }

        var profileTask = GetProfileAsync(normalizedSymbol, apiKey, cancellationToken);
        var floatTask = GetFloatSharesAsync(normalizedSymbol, apiKey, cancellationToken);
        var instTask = GetInstitutionalOwnershipAsync(normalizedSymbol, apiKey, cancellationToken);
        await Task.WhenAll(profileTask, floatTask, instTask);

        var factors = new FmpFactors
        {
            MarketCap = profileTask.Result,
            FloatShares = floatTask.Result,
            InstitutionalOwnership = instTask.Result
        };

        if (factors.MarketCap is null && factors.FloatShares is null && factors.InstitutionalOwnership is null)
        {
            return null;
        }

        _cache.Set(cacheKey, factors, _cacheWindow);
        _logger.LogInformation("FMP used for {Symbol}.", normalizedSymbol);
        return factors;
    }

    public async Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        _logger.LogInformation("[API] START → FmpService → {symbol}", normalizedSymbol);

        var cacheKey = $"price:{normalizedSymbol}:fmp";
        if (_cache.TryGetValue<QuoteResponse>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogInformation("[API] SUCCESS → FmpService");
            return cached;
        }

        var apiKey = _configuration["FMP__APIKEY"] ?? _configuration["Fmp:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("FMP__APIKEY missing. Continuing without Financial Modeling Prep quote fallback.");
            }

            _logger.LogWarning("[API] FAIL → FmpService → Missing API key");
            return null;
        }

        try
        {
            var uri = $"api/v3/quote/{normalizedSymbol}?apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[API] FAIL → FmpService → HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpQuoteItem>>(stream, JsonOptions, cancellationToken);
            var item = payload?.FirstOrDefault();
            if (item is null || item.Price is null || item.Price <= 0m)
            {
                _logger.LogWarning("[API] FAIL → FmpService → Empty or invalid quote payload");
                return null;
            }

            var previousClose = item.PreviousClose is > 0m ? item.PreviousClose.Value : item.Price.Value;
            var quote = new QuoteResponse
            {
                CurrentPrice = Math.Round(item.Price.Value, 2),
                PreviousClose = Math.Round(previousClose, 2),
                High = Math.Round(item.DayHigh ?? item.Price.Value, 2),
                Low = Math.Round(item.DayLow ?? item.Price.Value, 2),
                OpenPrice = Math.Round(item.Open ?? previousClose, 2),
                Volume = Math.Round(item.Volume ?? 0m, 0),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Provider = nameof(FmpService)
            };

            _cache.Set(cacheKey, quote, _quoteCacheWindow);
            _logger.LogInformation("[API] SUCCESS → FmpService");
            return quote;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("[API] FAIL → FmpService → {error}", ex.Message);
            return null;
        }
    }

    private async Task<decimal?> GetProfileAsync(string symbol, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var uri = $"api/v3/profile/{symbol}?apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpProfileItem>>(stream, JsonOptions, cancellationToken);
            return payload?.FirstOrDefault()?.MarketCap;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP profile fetch failed for {Symbol}.", symbol);
            return null;
        }
    }

    private async Task<decimal?> GetFloatSharesAsync(string symbol, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var uri = $"api/v4/shares_float?symbol={symbol}&apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpFloatSharesItem>>(stream, JsonOptions, cancellationToken);
            return payload?.FirstOrDefault()?.FloatShares;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP float-shares fetch failed for {Symbol}.", symbol);
            return null;
        }
    }

    private async Task<decimal?> GetInstitutionalOwnershipAsync(string symbol, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var uri = $"api/v4/institutional-ownership/extract-analytics/holder?symbol={symbol}&apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpInstitutionalOwnershipItem>>(stream, JsonOptions, cancellationToken);
            return payload?.FirstOrDefault()?.OwnershipPercent;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP institutional ownership fetch failed for {Symbol}.", symbol);
            return null;
        }
    }

    private sealed class FmpQuoteItem
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("previousClose")]
        public decimal? PreviousClose { get; set; }

        [JsonPropertyName("dayHigh")]
        public decimal? DayHigh { get; set; }

        [JsonPropertyName("dayLow")]
        public decimal? DayLow { get; set; }

        [JsonPropertyName("open")]
        public decimal? Open { get; set; }

        [JsonPropertyName("volume")]
        public decimal? Volume { get; set; }
    }
}
