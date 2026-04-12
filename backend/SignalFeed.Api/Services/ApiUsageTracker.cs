using System.Collections.Concurrent;
using System.Reflection;

namespace SignalFeed.Api.Services;

public sealed class ApiUsageTracker
{
    private readonly ConcurrentDictionary<string, ServiceCounters> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _baseUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _discoveredServices = new(StringComparer.Ordinal);

    public void DiscoverApiServices(Assembly assembly)
    {
        var discovered = assembly
            .GetTypes()
            .Where(type =>
                type.IsClass &&
                !type.IsAbstract &&
                type.Name.EndsWith("Service", StringComparison.Ordinal) &&
                type.GetConstructors().Any(ctor => ctor.GetParameters().Any(param => param.ParameterType == typeof(HttpClient))))
            .Select(type => type.Name);

        foreach (var serviceName in discovered)
        {
            _discoveredServices.TryAdd(serviceName, 0);
            _counters.TryAdd(serviceName, new ServiceCounters());
        }
    }

    public void RegisterConfiguredService(string serviceName, string? baseUrl = null)
    {
        _discoveredServices.TryAdd(serviceName, 0);
        _counters.TryAdd(serviceName, new ServiceCounters());

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _baseUrls.TryAdd(serviceName, baseUrl.TrimEnd('/'));
        }
    }

    public void RecordStart(string serviceName, Uri? requestUri)
    {
        var counters = _counters.GetOrAdd(serviceName, _ => new ServiceCounters());
        Interlocked.Increment(ref counters.TotalCalls);
        _discoveredServices.TryAdd(serviceName, 0);

        if (requestUri is null)
        {
            return;
        }

        var baseUrl = $"{requestUri.Scheme}://{requestUri.Host}";
        if (!requestUri.IsDefaultPort)
        {
            baseUrl = $"{baseUrl}:{requestUri.Port}";
        }

        _baseUrls.TryAdd(serviceName, baseUrl);
    }

    public void RecordSuccess(string serviceName)
    {
        var counters = _counters.GetOrAdd(serviceName, _ => new ServiceCounters());
        Interlocked.Increment(ref counters.SuccessCalls);
        _discoveredServices.TryAdd(serviceName, 0);
    }

    public void RecordFailure(string serviceName)
    {
        var counters = _counters.GetOrAdd(serviceName, _ => new ServiceCounters());
        Interlocked.Increment(ref counters.FailureCalls);
        _discoveredServices.TryAdd(serviceName, 0);
    }

    public void RecordRateLimit(string serviceName)
    {
        var counters = _counters.GetOrAdd(serviceName, _ => new ServiceCounters());
        Interlocked.Increment(ref counters.RateLimitHits);
        _discoveredServices.TryAdd(serviceName, 0);
    }

    public long GetRateLimitHits(string serviceName)
    {
        return _counters.TryGetValue(serviceName, out var counters)
            ? Interlocked.Read(ref counters.RateLimitHits)
            : 0;
    }

    public IReadOnlyList<ApiServiceUsageSnapshot> GetUsageSnapshot()
    {
        var allServiceNames = _discoveredServices.Keys
            .Concat(_counters.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var output = new List<ApiServiceUsageSnapshot>(allServiceNames.Count);
        foreach (var serviceName in allServiceNames)
        {
            _counters.TryGetValue(serviceName, out var counters);
            _baseUrls.TryGetValue(serviceName, out var baseUrl);
            output.Add(new ApiServiceUsageSnapshot
            {
                Service = serviceName,
                BaseUrl = baseUrl ?? string.Empty,
                Calls = counters is null ? 0 : Interlocked.Read(ref counters.TotalCalls),
                Success = counters is null ? 0 : Interlocked.Read(ref counters.SuccessCalls),
                Failures = counters is null ? 0 : Interlocked.Read(ref counters.FailureCalls),
                RateLimitHits = counters is null ? 0 : Interlocked.Read(ref counters.RateLimitHits)
            });
        }

        return output;
    }

    private sealed class ServiceCounters
    {
        public long TotalCalls;
        public long SuccessCalls;
        public long FailureCalls;
        public long RateLimitHits;
    }
}

public sealed class ApiServiceUsageSnapshot
{
    public string Service { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public long Calls { get; set; }

    public long Success { get; set; }

    public long Failures { get; set; }

    public long RateLimitHits { get; set; }
}
