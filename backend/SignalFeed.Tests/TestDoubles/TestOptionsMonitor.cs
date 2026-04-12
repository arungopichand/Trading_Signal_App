using Microsoft.Extensions.Options;

namespace SignalFeed.Tests.TestDoubles;

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private readonly T _value;

    public TestOptionsMonitor(T value)
    {
        _value = value;
    }

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
