using System.Collections.Concurrent;
using System.Text.Json;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FmpService
{
    private sealed record CachedFactors(FmpFactors Value, DateTimeOffset CachedAt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FmpService> _logger;
    private readonly ConcurrentDictionary<string, CachedFactors> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _cacheWindow = TimeSpan.FromMinutes(10);
    private int _missingKeyWarningLogged;

    public FmpService(HttpClient httpClient, IConfiguration configuration, ILogger<FmpService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FmpFactors?> GetFactorsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (_cache.TryGetValue(normalizedSymbol, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < _cacheWindow)
        {
            return cached.Value;
        }

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

        _cache[normalizedSymbol] = new CachedFactors(factors, DateTimeOffset.UtcNow);
        _logger.LogInformation("FMP used for {Symbol}.", normalizedSymbol);
        return factors;
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
}
