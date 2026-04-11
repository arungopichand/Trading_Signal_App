using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SignalBackgroundService : BackgroundService
{
    private static readonly TimeSpan NoDataWarningWindow = TimeSpan.FromSeconds(10);
    private readonly SignalEngine _signalEngine;
    private readonly FeedService _feedService;
    private readonly NewsService _newsService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly ILogger<SignalBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _newsInterval;
    private DateTimeOffset _nextNewsPull = DateTimeOffset.MinValue;

    public static IReadOnlyList<StockSignal> CachedSignals { get; private set; } = Array.Empty<StockSignal>();

    public SignalBackgroundService(
        SignalEngine signalEngine,
        FeedService feedService,
        NewsService newsService,
        SymbolUniverseService symbolUniverseService,
        IConfiguration configuration,
        ILogger<SignalBackgroundService> logger)
    {
        _signalEngine = signalEngine;
        _feedService = feedService;
        _newsService = newsService;
        _symbolUniverseService = symbolUniverseService;
        _logger = logger;
        _refreshInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Scanner:IntervalSeconds") ?? 15, 8, 60));
        _newsInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Scanner:NewsIntervalSeconds") ?? 60, 20, 300));
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
            var batchResult = await _signalEngine.GenerateSignalsAsync(stoppingToken);

            CachedSignals = batchResult.Signals;
            foreach (var signal in batchResult.Signals)
            {
                await _feedService.AddSignalAsync(signal, stoppingToken);
            }

            if (DateTimeOffset.UtcNow >= _nextNewsPull)
            {
                var symbols = CachedSignals
                    .OrderByDescending(signal => signal.Score)
                    .Select(signal => signal.Symbol)
                    .Distinct(StringComparer.Ordinal)
                    .Take(12)
                    .ToList();

                if (symbols.Count < 12)
                {
                    var universeSlice = await _symbolUniverseService.GetTopUniverseSliceAsync(
                        12 - symbols.Count,
                        0,
                        stoppingToken);
                    symbols.AddRange(universeSlice);
                }

                var newsItems = await _newsService.PullFreshNewsAsync(symbols, stoppingToken);
                foreach (var news in newsItems)
                {
                    await _feedService.AddNewsAsync(news, stoppingToken);
                }

                _nextNewsPull = DateTimeOffset.UtcNow.Add(_newsInterval);
            }

            _logger.LogInformation(
                "Updated {SignalCount} cached signals at {Timestamp}.",
                batchResult.Signals.Count,
                DateTimeOffset.Now);

            if (_feedService.IsRealDataStale(NoDataWarningWindow))
            {
                _logger.LogWarning(
                    "No real feed data received for over {Seconds} seconds.",
                    NoDataWarningWindow.TotalSeconds);
            }
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
