using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalFeed.Api.Services;

public sealed class FinnhubProviderProbeService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly FinnhubProviderState _state;
    private readonly ILogger<FinnhubProviderProbeService> _logger;

    public FinnhubProviderProbeService(
        HttpClient httpClient,
        IConfiguration configuration,
        FinnhubProviderState state,
        ILogger<FinnhubProviderProbeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = Random.Shared.Next(60, 121);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);

            if (_state.CanUseProvider || !_state.IsConfigured)
            {
                continue;
            }

            var token = FinnhubProviderState.ResolveApiKey(_configuration);
            if (string.IsNullOrWhiteSpace(token))
            {
                _state.MarkMissing(_logger);
                continue;
            }

            try
            {
                using var response = await _httpClient.GetAsync(
                    $"quote?symbol=SPY&token={Uri.EscapeDataString(token)}",
                    stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadBodyAsync(response, stoppingToken);
                    _state.RecordFailure(response.StatusCode, body, "half-open-probe", _logger);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                var quote = await JsonSerializer.DeserializeAsync<ProbeQuoteResponse>(stream, JsonOptions, stoppingToken);
                if (quote is null || quote.CurrentPrice <= 0m)
                {
                    _state.RecordFailure(HttpStatusCode.OK, "empty quote payload", "half-open-probe", _logger);
                    continue;
                }

                _state.RecordSuccess();
                _logger.LogInformation("Finnhub half-open probe succeeded. Provider re-enabled.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _state.RecordFailure(null, ex.Message, "half-open-probe", _logger, ex);
            }
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return string.Empty;
        }

        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ProbeQuoteResponse
    {
        [JsonPropertyName("c")]
        public decimal CurrentPrice { get; set; }
    }
}
