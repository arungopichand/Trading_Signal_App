namespace SignalFeed.Api.Services;

public sealed class UniverseRefreshBackgroundService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly ILogger<UniverseRefreshBackgroundService> _logger;

    public UniverseRefreshBackgroundService(
        SymbolUniverseService symbolUniverseService,
        ILogger<UniverseRefreshBackgroundService> logger)
    {
        _symbolUniverseService = symbolUniverseService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeRefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeRefreshAsync(stoppingToken);
        }
    }

    private async Task SafeRefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _symbolUniverseService.RefreshUniverseAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Symbol universe refresh service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Universe refresh failed.");
        }
    }
}
