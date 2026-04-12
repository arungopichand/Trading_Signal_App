using System.Net;
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
    private static readonly TimeSpan GeneralFailureCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AuthFailureCooldown = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(3);
    private const int ConsecutiveFailureThreshold = 5;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FmpService> _logger;
    private readonly object _stateGate = new();
    private readonly TimeSpan _cacheWindow = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _quoteCacheWindow = TimeSpan.FromSeconds(5);
    private int _consecutiveFailures;
    private DateTimeOffset _disabledUntil = DateTimeOffset.MinValue;
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
        if (IsTemporarilyDisabled(out var disabledFor))
        {
            _logger.LogInformation(
                "FMP temporarily disabled for {Seconds}s. Skipping fundamentals for {Symbol}.",
                Math.Ceiling(disabledFor.TotalSeconds),
                normalizedSymbol);
            return null;
        }

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
            RegisterFailure(null, "factors-empty", normalizedSymbol);
            return null;
        }

        _cache.Set(cacheKey, factors, _cacheWindow);
        RegisterSuccess();
        _logger.LogInformation("FMP used for {Symbol}.", normalizedSymbol);
        return factors;
    }

    public async Task<QuoteResponse?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (IsTemporarilyDisabled(out var disabledFor))
        {
            _logger.LogInformation(
                "[API] SKIP -> FmpService -> cooldown active for {Seconds}s ({symbol})",
                Math.Ceiling(disabledFor.TotalSeconds),
                normalizedSymbol);
            return null;
        }

        _logger.LogInformation("[API] START -> FmpService -> {symbol}", normalizedSymbol);

        var cacheKey = $"price:{normalizedSymbol}:fmp";
        if (_cache.TryGetValue<QuoteResponse>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogInformation("[API] SUCCESS -> FmpService");
            return cached;
        }

        var apiKey = _configuration["FMP__APIKEY"] ?? _configuration["Fmp:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("FMP__APIKEY missing. Continuing without Financial Modeling Prep quote fallback.");
            }

            _logger.LogWarning("[API] FAIL -> FmpService -> Missing API key");
            return null;
        }

        try
        {
            var uri = $"api/v3/quote/{normalizedSymbol}?apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                RegisterFailure(response.StatusCode, "quote-http", normalizedSymbol);
                _logger.LogWarning("[API] FAIL -> FmpService -> HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpQuoteItem>>(stream, JsonOptions, cancellationToken);
            var item = payload?.FirstOrDefault();
            if (item is null || item.Price is null || item.Price <= 0m)
            {
                RegisterFailure(null, "quote-empty", normalizedSymbol);
                _logger.LogWarning("[API] FAIL -> FmpService -> Empty or invalid quote payload");
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
            RegisterSuccess();
            _logger.LogInformation("[API] SUCCESS -> FmpService");
            return quote;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            RegisterFailure(null, "quote-exception", normalizedSymbol);
            _logger.LogWarning("[API] FAIL -> FmpService -> {error}", ex.Message);
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
                RegisterFailure(response.StatusCode, "profile-http", symbol);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpProfileItem>>(stream, JsonOptions, cancellationToken);
            var value = payload?.FirstOrDefault()?.MarketCap;
            if (value is null)
            {
                RegisterFailure(null, "profile-empty", symbol);
            }

            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP profile fetch failed for {Symbol}.", symbol);
            RegisterFailure(null, "profile-exception", symbol);
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
                RegisterFailure(response.StatusCode, "float-http", symbol);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpFloatSharesItem>>(stream, JsonOptions, cancellationToken);
            var value = payload?.FirstOrDefault()?.FloatShares;
            if (value is null)
            {
                RegisterFailure(null, "float-empty", symbol);
            }

            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP float-shares fetch failed for {Symbol}.", symbol);
            RegisterFailure(null, "float-exception", symbol);
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
                RegisterFailure(response.StatusCode, "institutional-http", symbol);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<FmpInstitutionalOwnershipItem>>(stream, JsonOptions, cancellationToken);
            var value = payload?.FirstOrDefault()?.OwnershipPercent;
            if (value is null)
            {
                RegisterFailure(null, "institutional-empty", symbol);
            }

            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "FMP institutional ownership fetch failed for {Symbol}.", symbol);
            RegisterFailure(null, "institutional-exception", symbol);
            return null;
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

    private void RegisterFailure(HttpStatusCode? statusCode, string context, string symbol)
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
                    "FMP {Context} unauthorized/forbidden for {Symbol}. Cooling down for {Minutes}m.",
                    context,
                    symbol,
                    AuthFailureCooldown.TotalMinutes);
                return;
            }

            if (statusCode == (HttpStatusCode)429)
            {
                _disabledUntil = now.Add(RateLimitCooldown);
                _consecutiveFailures = 0;
                _logger.LogWarning(
                    "FMP {Context} rate-limited for {Symbol}. Cooling down for {Minutes}m.",
                    context,
                    symbol,
                    RateLimitCooldown.TotalMinutes);
                return;
            }

            if (_consecutiveFailures >= ConsecutiveFailureThreshold)
            {
                _disabledUntil = now.Add(GeneralFailureCooldown);
                _consecutiveFailures = 0;
                _logger.LogWarning(
                    "FMP {Context} repeated failures reached threshold. Cooling down for {Minutes}m.",
                    context,
                    GeneralFailureCooldown.TotalMinutes);
            }
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
