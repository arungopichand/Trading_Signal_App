using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SignalFeed.Tests.TestDoubles;

internal sealed class RouteHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    private readonly ConcurrentDictionary<string, int> _hitCount = new(StringComparer.OrdinalIgnoreCase);

    public RouteHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public int TotalCalls => _hitCount.Values.Sum();

    public int GetCallCount(string keyPrefix)
    {
        return _hitCount
            .Where(pair => pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .Sum(pair => pair.Value);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.AbsolutePath ?? "/";
        _hitCount.AddOrUpdate(key, 1, (_, current) => current + 1);
        return await _handler(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
