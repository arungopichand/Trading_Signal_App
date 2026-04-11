using System.Text.Json;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class PolygonService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PolygonService> _logger;
    private int _missingKeyWarningLogged;

    public PolygonService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PolygonService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PolygonAggregateBar?> GetPreviousAggregateAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["POLYGON__APIKEY"] ?? _configuration["Polygon:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("POLYGON__APIKEY missing. Continuing with fallback sources.");
            }

            return null;
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var requestUri = $"v2/aggs/ticker/{normalizedSymbol}/prev?adjusted=true&apiKey={Uri.EscapeDataString(apiKey)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<PolygonAggregateResponse>(stream, JsonOptions, cancellationToken);
            if (payload is null || payload.Results.Count == 0)
            {
                return null;
            }

            return payload.Results[0];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Polygon aggregate fetch failed for {Symbol}.", symbol);
            return null;
        }
    }

    public async Task<PolygonSnapshotTicker?> GetSnapshotAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["POLYGON__APIKEY"] ?? _configuration["Polygon:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("POLYGON__APIKEY missing. Continuing with fallback sources.");
            }

            return null;
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var requestUri = $"v2/snapshot/locale/us/markets/stocks/tickers/{normalizedSymbol}?apiKey={Uri.EscapeDataString(apiKey)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<PolygonSnapshotResponse>(stream, JsonOptions, cancellationToken);
            if (payload is null ||
                !string.Equals(payload.Status, "OK", StringComparison.OrdinalIgnoreCase) ||
                payload.Ticker is null)
            {
                return null;
            }

            return payload.Ticker;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Polygon snapshot fetch failed for {Symbol}.", symbol);
            return null;
        }
    }
}
