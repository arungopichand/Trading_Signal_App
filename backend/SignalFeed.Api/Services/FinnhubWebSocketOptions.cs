namespace SignalFeed.Api.Services;

public sealed class FinnhubWebSocketOptions
{
    public string Token { get; set; } = string.Empty;
    public int StaleAfterSeconds { get; set; } = 15;
    public int MaxSubscribedSymbols { get; set; } = 50;
    public int SymbolTtlSeconds { get; set; } = 600;
    public int CleanupIntervalSeconds { get; set; } = 30;
    public int InitialReconnectDelayMs { get; set; } = 1000;
    public int MaxReconnectDelayMs { get; set; } = 30000;
}
