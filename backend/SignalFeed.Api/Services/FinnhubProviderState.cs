using System.Net;

namespace SignalFeed.Api.Services;

public enum FinnhubErrorKind
{
    MissingKey = 0,
    InvalidKey = 1,
    RateLimited = 2,
    Network = 3,
    Unknown = 4
}

public static class FinnhubErrorClassifier
{
    private static readonly string[] InvalidKeyMarkers =
    [
        "invalid api key",
        "you must be granted a valid key",
        "invalid token",
        "missing token",
        "unauthorized"
    ];

    public static FinnhubErrorKind Classify(HttpStatusCode? statusCode, string? responseBody, Exception? exception = null)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return FinnhubErrorKind.InvalidKey;
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return FinnhubErrorKind.RateLimited;
        }

        if (!string.IsNullOrWhiteSpace(responseBody) &&
            (InvalidKeyMarkers.Any(marker => responseBody.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
             responseBody.Contains("401", StringComparison.Ordinal) ||
             responseBody.Contains("403", StringComparison.Ordinal)))
        {
            return FinnhubErrorKind.InvalidKey;
        }

        if (exception is HttpRequestException or TaskCanceledException)
        {
            return FinnhubErrorKind.Network;
        }

        return FinnhubErrorKind.Unknown;
    }
}

public sealed class FinnhubProviderState
{
    private readonly object _gate = new();
    private bool _configured;
    private bool _valid;
    private string? _lastError;
    private FinnhubErrorKind? _lastErrorKind;
    private DateTimeOffset? _lastSuccessUtc;
    private long _successCount;
    private long _failureCount;
    private long _keyInvalidCount;
    private long _rateLimitHits;

    public bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                return _configured;
            }
        }
    }

    public bool IsValid
    {
        get
        {
            lock (_gate)
            {
                return _valid;
            }
        }
    }

    public bool CanUseProvider
    {
        get
        {
            lock (_gate)
            {
                return _configured && _valid;
            }
        }
    }

    public void Initialize(string? apiKey, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MarkMissing(logger);
            return;
        }

        lock (_gate)
        {
            _configured = true;
            _valid = true;
            _lastError = null;
            _lastErrorKind = null;
        }
    }

    public void MarkMissing(ILogger? logger = null)
    {
        lock (_gate)
        {
            _configured = false;
            _valid = false;
            _lastError = "FINNHUB KEY MISSING";
            _lastErrorKind = FinnhubErrorKind.MissingKey;
            _lastSuccessUtc = null;
            Interlocked.Increment(ref _failureCount);
        }

        logger?.LogCritical("FINNHUB KEY MISSING. Finnhub provider disabled.");
    }

    public void MarkInvalid(string reason, ILogger? logger = null)
    {
        lock (_gate)
        {
            _configured = true;
            _valid = false;
            _lastError = string.IsNullOrWhiteSpace(reason) ? "FINNHUB KEY INVALID" : reason;
            _lastErrorKind = FinnhubErrorKind.InvalidKey;
            Interlocked.Increment(ref _failureCount);
            Interlocked.Increment(ref _keyInvalidCount);
        }

        logger?.LogError("FINNHUB KEY INVALID. {Reason}", _lastError);
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _configured = true;
            _valid = true;
            _lastError = null;
            _lastErrorKind = null;
            _lastSuccessUtc = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _successCount);
        }
    }

    public void RecordRateLimit(ILogger? logger = null)
    {
        lock (_gate)
        {
            _lastError = "FINNHUB RATE LIMIT HIT";
            _lastErrorKind = FinnhubErrorKind.RateLimited;
            Interlocked.Increment(ref _rateLimitHits);
        }

        logger?.LogWarning("FINNHUB RATE LIMIT HIT.");
    }

    public void RecordFailure(
        HttpStatusCode? statusCode,
        string? responseBody,
        string context,
        ILogger? logger = null,
        Exception? exception = null)
    {
        var kind = FinnhubErrorClassifier.Classify(statusCode, responseBody, exception);
        switch (kind)
        {
            case FinnhubErrorKind.MissingKey:
                MarkMissing(logger);
                return;
            case FinnhubErrorKind.InvalidKey:
                MarkInvalid($"FINNHUB KEY INVALID ({context})", logger);
                return;
            case FinnhubErrorKind.RateLimited:
                RecordRateLimit(logger);
                break;
            case FinnhubErrorKind.Network:
                logger?.LogWarning(exception, "Finnhub network failure in {Context}.", context);
                break;
            default:
                break;
        }

        lock (_gate)
        {
            _lastError = $"FINNHUB FAILURE ({context})";
            _lastErrorKind = kind;
            Interlocked.Increment(ref _failureCount);
        }
    }

    public FinnhubHealthSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var lastSuccessSecondsAgo = _lastSuccessUtc.HasValue
                ? Math.Max(0, (long)(DateTimeOffset.UtcNow - _lastSuccessUtc.Value).TotalSeconds)
                : (long?)null;

            return new FinnhubHealthSnapshot
            {
                Configured = _configured,
                Valid = _valid,
                LastError = _lastError,
                LastErrorKind = _lastErrorKind?.ToString(),
                LastSuccessSecondsAgo = lastSuccessSecondsAgo,
                FinnhubSuccessCount = Interlocked.Read(ref _successCount),
                FinnhubFailureCount = Interlocked.Read(ref _failureCount),
                FinnhubKeyInvalidCount = Interlocked.Read(ref _keyInvalidCount),
                FinnhubRateLimitHits = Interlocked.Read(ref _rateLimitHits)
            };
        }
    }

    public static string ResolveApiKey(IConfiguration configuration)
    {
        return Environment.GetEnvironmentVariable("FINNHUB_API_KEY")
            ?? Environment.GetEnvironmentVariable("FINNHUB__APIKEY")
            ?? configuration["FINNHUB_API_KEY"]
            ?? configuration["FINNHUB__APIKEY"]
            ?? configuration["Finnhub:ApiKey"]
            ?? string.Empty;
    }
}

public sealed class FinnhubHealthSnapshot
{
    public bool Configured { get; set; }

    public bool Valid { get; set; }

    public string? LastError { get; set; }

    public string? LastErrorKind { get; set; }

    public long? LastSuccessSecondsAgo { get; set; }

    public long FinnhubSuccessCount { get; set; }

    public long FinnhubFailureCount { get; set; }

    public long FinnhubKeyInvalidCount { get; set; }

    public long FinnhubRateLimitHits { get; set; }
}
