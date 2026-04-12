using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SignalBackgroundService : BackgroundService
{
    private static readonly TimeSpan NoDataWarningWindow = TimeSpan.FromSeconds(10);
    private readonly SignalEngine _signalEngine;
    private readonly FeedService _feedService;
    private readonly MarketDataService _marketDataService;
    private readonly NewsAggregationService _newsAggregationService;
    private readonly SymbolUniverseService _symbolUniverseService;
    private readonly ILogger<SignalBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _newsInterval;
    private DateTimeOffset _nextNewsPull = DateTimeOffset.MinValue;

    public static IReadOnlyList<StockSignal> CachedSignals { get; private set; } = Array.Empty<StockSignal>();

    public SignalBackgroundService(
        SignalEngine signalEngine,
        FeedService feedService,
        MarketDataService marketDataService,
        NewsAggregationService newsAggregationService,
        SymbolUniverseService symbolUniverseService,
        IConfiguration configuration,
        ILogger<SignalBackgroundService> logger)
    {
        _signalEngine = signalEngine;
        _feedService = feedService;
        _marketDataService = marketDataService;
        _newsAggregationService = newsAggregationService;
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
            var signalsForCache = batchResult.Signals.ToList();
            foreach (var signal in batchResult.Signals)
            {
                await _feedService.AddSignalAsync(signal, stoppingToken);
            }

            if (batchResult.Signals.Count == 0)
            {
                var fallbackSignal = new StockSignal
                {
                    Symbol = "SPY",
                    CountryCode = "US",
                    Price = 0m,
                    PriceRange = "N/A",
                    ChangePercent = 0m,
                    SignalType = "TRENDING",
                    Score = 5m,
                    ActivityScore = 5m,
                    Confidence = "LOW",
                    TradeReadiness = "WATCH",
                    Headline = "Market heartbeat: waiting for stronger live confluence signals.",
                    SignalReason = "Fallback heartbeat signal keeps feed active.",
                    Reasons = ["Fallback heartbeat"],
                    Sentiment = "NEUTRAL",
                    Source = "SYSTEM",
                    Timestamp = DateTimeOffset.UtcNow,
                    ScannedAt = DateTimeOffset.UtcNow,
                    IsTrending = true,
                    RepeatCount = 1
                };
                signalsForCache.Add(fallbackSignal);

                await _feedService.AddItemAsync(new FeedItem
                {
                    Symbol = "SPY",
                    CountryCode = "US",
                    Price = 0m,
                    PriceRange = "N/A",
                    ChangePercent = 0m,
                    SignalType = "TRENDING",
                    Score = 5m,
                    ActivityScore = 5m,
                    Confidence = "LOW",
                    TradeReadiness = "WATCH",
                    Headline = "Market heartbeat: waiting for stronger live confluence signals.",
                    Reason = "Fallback heartbeat signal keeps feed active.",
                    Reasons = ["Fallback heartbeat"],
                    Sentiment = "NEUTRAL",
                    Source = "SYSTEM",
                    Timestamp = DateTimeOffset.UtcNow
                }, stoppingToken);
            }

            CachedSignals = signalsForCache;

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

                var newsItems = await _newsAggregationService.PullFreshNewsAsync(symbols, stoppingToken);
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

            await _marketDataService.PersistProviderPerformanceSnapshotAsync(stoppingToken);
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
