using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SignalBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;

    public static IReadOnlyList<StockSignal> CachedSignals { get; private set; } = Array.Empty<StockSignal>();

    public SignalBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SignalBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _refreshInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Scanner:IntervalSeconds") ?? 15, 15, 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSignalsAsync(stoppingToken);

        using var timer = new PeriodicTimer(_refreshInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshSignalsAsync(stoppingToken);
        }
    }

    private async Task RefreshSignalsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<SignalEngine>();
            var batchResult = await engine.GenerateSignalsAsync(stoppingToken);

            CachedSignals = batchResult.Signals;

            _logger.LogInformation(
                "Updated {SignalCount} cached signals at {Timestamp}.",
                batchResult.Signals.Count,
                DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Signal background service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signal refresh failed. Keeping the last cached results.");
        }
    }
}
