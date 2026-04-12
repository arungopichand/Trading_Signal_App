using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SupabaseDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int SymbolQueryLimit = 500;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupabaseDataService> _logger;

    public SupabaseDataService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<SupabaseDataService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TrackedSymbol>> GetActiveSymbolsAsync(CancellationToken cancellationToken = default)
    {
        return await GetSymbolsInternalAsync(activeOnly: true, cancellationToken);
    }

    public async Task<IReadOnlyList<TrackedSymbol>> GetSymbolsAsync(CancellationToken cancellationToken = default)
    {
        return await GetSymbolsInternalAsync(activeOnly: false, cancellationToken);
    }

    public async Task<TrackedSymbol?> AddSymbolAsync(CreateTrackedSymbolRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryGetSupabaseSettings(out var baseUrl, out var apiKey) || string.IsNullOrWhiteSpace(request.Symbol))
        {
            return null;
        }

        var payload = new[]
        {
            new CreateTrackedSymbolDto
            {
                Symbol = request.Symbol.Trim().ToUpperInvariant(),
                IsActive = request.IsActive
            }
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{baseUrl}/rest/v1/tracked_symbols",
            apiKey,
            payload,
            cancellationToken,
            "return=representation");

        if (!response.IsSuccessStatusCode)
        {
            await LogSupabaseFailureAsync("add symbol", response);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var created = await JsonSerializer.DeserializeAsync<List<TrackedSymbolDto>>(stream, JsonOptions, cancellationToken);
        return created?.Select(MapTrackedSymbol).FirstOrDefault();
    }

    public async Task<bool> SetSymbolActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        if (!TryGetSupabaseSettings(out var baseUrl, out var apiKey))
        {
            return false;
        }

        var payload = new UpdateTrackedSymbolActiveDto
        {
            IsActive = isActive
        };

        using var response = await SendAsync(
            HttpMethod.Patch,
            $"{baseUrl}/rest/v1/tracked_symbols?id=eq.{id}",
            apiKey,
            payload,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await LogSupabaseFailureAsync($"set symbol {id} active state", response);
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<TrackedSymbol>> GetSymbolsInternalAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        if (!TryGetSupabaseSettings(out var baseUrl, out var apiKey))
        {
            return [];
        }

        var activeFilter = activeOnly ? "&is_active=eq.true" : string.Empty;
        var url = $"{baseUrl}/rest/v1/tracked_symbols?select=id,symbol,is_active,created_at{activeFilter}&order=symbol.asc&limit={SymbolQueryLimit}";

        using var response = await SendAsync(HttpMethod.Get, url, apiKey, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await LogSupabaseFailureAsync("read tracked symbols", response);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var symbols = await JsonSerializer.DeserializeAsync<List<TrackedSymbolDto>>(stream, JsonOptions, cancellationToken);
        return symbols?.Select(MapTrackedSymbol).ToList() ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string apiKey,
        object? payload,
        CancellationToken cancellationToken,
        string? prefer = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("apikey", apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (!string.IsNullOrWhiteSpace(prefer))
        {
            request.Headers.Add("Prefer", prefer);
        }

        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, cancellationToken);
        sw.Stop();

        if (sw.ElapsedMilliseconds > 300)
        {
            var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            _logger.LogWarning(
                "SLOW_SUPABASE_QUERY {Method} {Endpoint} -> {StatusCode} in {ElapsedMs}ms",
                method.Method,
                endpoint,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds);
        }

        return response;
    }

    private bool TryGetSupabaseSettings(out string baseUrl, out string apiKey)
    {
        baseUrl = (
            Environment.GetEnvironmentVariable("SUPABASE_URL")
            ?? _configuration["SUPABASE_URL"]
            ?? _configuration["Supabase:Url"]
            ?? string.Empty).TrimEnd('/');
        apiKey =
            Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY")
            ?? _configuration["SUPABASE_SERVICE_KEY"]
            ?? _configuration["Supabase:ServiceKey"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Supabase is not configured. Returning an empty symbol universe.");
            return false;
        }

        return true;
    }

    private async Task LogSupabaseFailureAsync(string operation, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogWarning(
            "Supabase {Operation} failed with status code {StatusCode}. Response: {ResponseBody}",
            operation,
            response.StatusCode,
            body);
    }

    private static TrackedSymbol MapTrackedSymbol(TrackedSymbolDto dto)
    {
        return new TrackedSymbol
        {
            Id = dto.Id,
            Symbol = dto.Symbol ?? string.Empty,
            IsActive = dto.IsActive,
            CreatedAt = dto.CreatedAt
        };
    }

    private sealed class TrackedSymbolDto
    {
        public Guid Id { get; set; }

        public string? Symbol { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class CreateTrackedSymbolDto
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }

    private sealed class UpdateTrackedSymbolActiveDto
    {
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }
}
