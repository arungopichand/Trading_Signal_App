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
    private int _newsRefreshInFlight;
    private static long _scanCycleCount;
    private static long _lastScanDurationMs;
    private static long _totalScanDurationMs;
    private static long _lastSuccessfulScanUnixMs;

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
        _refreshInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Scanner:IntervalSeconds") ?? 6, 3, 60));
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
        var cycleStartedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var batchResult = await _signalEngine.GenerateSignalsAsync(stoppingToken);
            var signalsForCache = batchResult.Signals.ToList();
            var signalTasks = batchResult.Signals
                .Select(signal => _feedService.AddSignalAsync(signal, stoppingToken))
                .ToArray();
            await Task.WhenAll(signalTasks);

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

            if (DateTimeOffset.UtcNow >= _nextNewsPull && Interlocked.CompareExchange(ref _newsRefreshInFlight, 1, 0) == 0)
            {
                _nextNewsPull = DateTimeOffset.UtcNow.Add(_newsInterval);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PullNewsAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // no-op
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Async news pull failed.");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _newsRefreshInFlight, 0);
                    }
                }, CancellationToken.None);
            }

            _logger.LogDebug(
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
            sw.Stop();
            var elapsedMs = sw.ElapsedMilliseconds;
            Interlocked.Increment(ref _scanCycleCount);
            Interlocked.Exchange(ref _lastScanDurationMs, elapsedMs);
            Interlocked.Add(ref _totalScanDurationMs, elapsedMs);
            Interlocked.Exchange(ref _lastSuccessfulScanUnixMs, cycleStartedAt.ToUnixTimeMilliseconds());
            _logger.LogDebug("Signal scan cycle completed in {ElapsedMs}ms.", elapsedMs);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Signal background service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signal refresh failed. Keeping the last cached results.");
        }
    }

    private async Task PullNewsAsync(CancellationToken stoppingToken)
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
        var newsTasks = newsItems
            .Select(news => _feedService.AddNewsAsync(news, stoppingToken))
            .ToArray();
        await Task.WhenAll(newsTasks);
    }

    public static SignalScannerRuntimeSnapshot GetRuntimeSnapshot()
    {
        var cycles = Interlocked.Read(ref _scanCycleCount);
        var totalMs = Interlocked.Read(ref _totalScanDurationMs);
        var averageMs = cycles > 0 ? totalMs / (double)cycles : 0d;
        var lastSuccessUnixMs = Interlocked.Read(ref _lastSuccessfulScanUnixMs);
        var lastSuccessfulScanAt = lastSuccessUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastSuccessUnixMs)
            : (DateTimeOffset?)null;
        return new SignalScannerRuntimeSnapshot
        {
            ScanCycleCount = cycles,
            LastScanDurationMs = Interlocked.Read(ref _lastScanDurationMs),
            AverageScanDurationMs = Math.Round(averageMs, 2),
            LastSuccessfulScanAt = lastSuccessfulScanAt
        };
    }
}

public sealed class SignalScannerRuntimeSnapshot
{
    public long ScanCycleCount { get; set; }

    public long LastScanDurationMs { get; set; }

    public double AverageScanDurationMs { get; set; }

    public DateTimeOffset? LastSuccessfulScanAt { get; set; }
}
