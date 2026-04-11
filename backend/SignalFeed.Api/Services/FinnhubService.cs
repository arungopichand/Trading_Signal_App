using System.Text.Json;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class FinnhubService
{
    private sealed record CachedQuote(QuoteResponse Quote, DateTimeOffset CachedAt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly Queue<DateTimeOffset> RequestTimestamps = new();
    private static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuoteCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuoteCooldown = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, CachedQuote> QuoteCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTimeOffset> QuoteCooldowns = new(StringComparer.Ordinal);
    private const int MaxRequestsPerSecond = 3;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<FinnhubService> _logger;
    private int _missingKeyWarningLogged;

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
            using var response = await GetWithThrottleAsync($"news?category=general&token={apiKey}");
            if (response is null)
            {
                return [];
            }

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
        var now = DateTimeOffset.UtcNow;

        lock (QuoteCache)
        {
            if (QuoteCooldowns.TryGetValue(encodedSymbol, out var cooldownUntil))
            {
                if (cooldownUntil > now)
                {
                    _logger.LogDebug(
                        "Quote request skipped for {Symbol}; cooldown active for {Seconds} more seconds.",
                        encodedSymbol,
                        Math.Ceiling((cooldownUntil - now).TotalSeconds));
                    return null;
                }

                QuoteCooldowns.Remove(encodedSymbol);
            }

            if (QuoteCache.TryGetValue(encodedSymbol, out var cached) && now - cached.CachedAt < QuoteCacheTtl)
            {
                return cached.Quote;
            }
        }

        try
        {
            using var response = await GetWithThrottleAsync(
                $"quote?symbol={encodedSymbol}&token={apiKey}",
                symbolForCooldown: encodedSymbol);
            if (response is null)
            {
                return null;
            }

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
            var quote = await JsonSerializer.DeserializeAsync<QuoteResponse>(stream, JsonOptions);
            if (quote is not null)
            {
                lock (QuoteCache)
                {
                    QuoteCache[encodedSymbol] = new CachedQuote(quote, DateTimeOffset.UtcNow);
                }
            }

            return quote;
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

    public async Task<CompanyProfileResponse?> GetCompanyProfileAsync(string symbol)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return null;
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        try
        {
            using var response = await GetWithThrottleAsync($"stock/profile2?symbol={encodedSymbol}&token={apiKey}");
            if (response is null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<CompanyProfileResponse>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to load company profile for {Symbol}.", encodedSymbol);
            return null;
        }
    }

    public async Task<BasicFinancialsResponse?> GetBasicFinancialsAsync(string symbol)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return null;
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        try
        {
            using var response = await GetWithThrottleAsync(
                $"stock/metric?symbol={encodedSymbol}&metric=all&token={apiKey}");
            if (response is null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<BasicFinancialsResponse>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to load basic financials for {Symbol}.", encodedSymbol);
            return null;
        }
    }

    public async Task<CandleResponse?> GetCandlesAsync(
        string symbol,
        string resolution,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return null;
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());
        var fromUnix = from.ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();

        try
        {
            using var response = await GetWithThrottleAsync(
                $"stock/candle?symbol={encodedSymbol}&resolution={resolution}&from={fromUnix}&to={toUnix}&token={apiKey}");
            if (response is null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var candles = await JsonSerializer.DeserializeAsync<CandleResponse>(stream, JsonOptions);
            return candles?.Status == "ok" ? candles : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to load candles for {Symbol}.", encodedSymbol);
            return null;
        }
    }

    public async Task<NewsItem?> GetLatestCompanyNewsAsync(string symbol)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return null;
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());
        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-14);

        try
        {
            using var response = await GetWithThrottleAsync(
                $"company-news?symbol={encodedSymbol}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={apiKey}");
            if (response is null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var items = await JsonSerializer.DeserializeAsync<List<NewsItem>>(stream, JsonOptions);
            return items?
                .Where(item => !string.IsNullOrWhiteSpace(item.Headline) && !string.IsNullOrWhiteSpace(item.Url))
                .OrderByDescending(item => item.Datetime)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to load company news for {Symbol}.", encodedSymbol);
            return null;
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetCompanyNewsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return [];
        }

        var encodedSymbol = Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        try
        {
            using var response = await GetWithThrottleAsync(
                $"company-news?symbol={encodedSymbol}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={apiKey}",
                cancellationToken);
            if (response is null)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var items = await JsonSerializer.DeserializeAsync<List<NewsItem>>(stream, JsonOptions, cancellationToken);
            return items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to load company news for {Symbol}.", encodedSymbol);
            return [];
        }
    }

    public async Task<IReadOnlyList<FinnhubSymbol>> GetUsSymbolsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            return [];
        }

        try
        {
            using var response = await GetWithThrottleAsync($"stock/symbol?exchange=US&token={apiKey}", cancellationToken);
            if (response is null)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Finnhub symbol universe request failed with {StatusCode}.", response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var items = await JsonSerializer.DeserializeAsync<List<FinnhubSymbol>>(stream, JsonOptions, cancellationToken);
            return items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to fetch Finnhub symbol universe.");
            return [];
        }
    }

    public string? GetApiKey()
    {
        return TryGetApiKey(out var apiKey) ? apiKey : null;
    }

    private async Task<HttpResponseMessage?> GetWithThrottleAsync(
        string requestUri,
        CancellationToken cancellationToken = default,
        string? symbolForCooldown = null)
    {
        await WaitForThrottleSlotAsync(cancellationToken);

        var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
            !string.IsNullOrWhiteSpace(symbolForCooldown))
        {
            var until = DateTimeOffset.UtcNow.Add(QuoteCooldown);
            lock (QuoteCache)
            {
                QuoteCooldowns[symbolForCooldown] = until;
            }

            _logger.LogWarning(
                "Finnhub returned 429 for {Symbol}. Cooling down symbol until {CooldownUntil}.",
                symbolForCooldown,
                until);
        }

        return response;
    }

    private static async Task WaitForThrottleSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan? wait = null;
            await RequestGate.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (RequestTimestamps.Count > 0 && now - RequestTimestamps.Peek() >= RequestWindow)
                {
                    RequestTimestamps.Dequeue();
                }

                if (RequestTimestamps.Count < MaxRequestsPerSecond)
                {
                    RequestTimestamps.Enqueue(now);
                    return;
                }

                wait = RequestWindow - (now - RequestTimestamps.Peek());
                if (wait < TimeSpan.FromMilliseconds(25))
                {
                    wait = TimeSpan.FromMilliseconds(25);
                }
            }
            finally
            {
                RequestGate.Release();
            }

            await Task.Delay(wait!.Value, cancellationToken);
        }
    }

    private bool TryGetApiKey(out string apiKey)
    {
        apiKey =
            Environment.GetEnvironmentVariable("FINNHUB__APIKEY")
            ?? _config["FINNHUB__APIKEY"]
            ?? _config["Finnhub:ApiKey"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (Interlocked.Exchange(ref _missingKeyWarningLogged, 1) == 0)
            {
                _logger.LogWarning("FINNHUB__APIKEY missing. Continuing with available data sources.");
            }

            return false;
        }

        return true;
    }
}
