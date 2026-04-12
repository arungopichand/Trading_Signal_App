namespace SignalFeed.Api.Services;

public sealed class ApiCallLoggingHandler : DelegatingHandler
{
    private readonly ApiUsageTracker _usageTracker;
    private readonly ILogger _logger;
    private readonly string _serviceName;

    public ApiCallLoggingHandler(
        ApiUsageTracker usageTracker,
        ILoggerFactory loggerFactory,
        string serviceName)
    {
        _usageTracker = usageTracker;
        _logger = loggerFactory.CreateLogger<ApiCallLoggingHandler>();
        _serviceName = serviceName;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var symbol = ExtractSymbol(request.RequestUri);
        _logger.LogInformation("API_CALL_START: {ServiceName} -> {symbol}", _serviceName, symbol);
        _usageTracker.RecordStart(_serviceName, request.RequestUri);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("API_CALL_SUCCESS: {ServiceName}", _serviceName);
                _usageTracker.RecordSuccess(_serviceName);
            }
            else
            {
                _logger.LogWarning("API_CALL_FAIL: {ServiceName} -> HTTP {StatusCode}", _serviceName, (int)response.StatusCode);
                _usageTracker.RecordFailure(_serviceName);
                if ((int)response.StatusCode == 429)
                {
                    _usageTracker.RecordRateLimit(_serviceName);
                }
            }

            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("API_CALL_FAIL: {ServiceName} -> {Error}", _serviceName, ex.Message);
            _usageTracker.RecordFailure(_serviceName);
            throw;
        }
    }

    private static string ExtractSymbol(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return "-";
        }

        var query = requestUri.Query;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var values = query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (var pair in values)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(parts[0]).Trim();
                var value = Uri.UnescapeDataString(parts[1]).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (key.Contains("symbol", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("ticker", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        var segments = requestUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length is >= 1 and <= 8)
            .Where(segment => segment.All(ch => char.IsLetterOrDigit(ch)))
            .ToList();

        var likelyTicker = segments.LastOrDefault();
        return string.IsNullOrWhiteSpace(likelyTicker) ? "-" : likelyTicker;
    }
}
