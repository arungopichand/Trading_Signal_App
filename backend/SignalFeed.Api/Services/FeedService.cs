using Microsoft.AspNetCore.SignalR;
using SignalFeed.Api.Hubs;
using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class FeedService
{
    private const int MaxItems = 100;
    private static readonly TimeSpan ExpireWindow = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ScoreDecayStart = TimeSpan.FromSeconds(30);
    private const decimal MaxScoreDecay = 0.55m;
    private static readonly TimeSpan SymbolReemitWindow = TimeSpan.FromSeconds(30);
    private const decimal SymbolScoreJumpThreshold = 25m;
    private static readonly TimeSpan TrendingWindow = TimeSpan.FromMinutes(5);
    private const int TrendingThreshold = 3;
    private readonly TimeSpan _emitSpacing;
    private readonly TimeSpan _liveFreshWindow;
    private readonly TimeSpan _pollingFreshWindow;
    private readonly TimeSpan _batchWindow;
    private readonly int _maxBatchSize;
    private readonly bool _allowPollingWhileLiveFresh;
    private readonly IHubContext<FeedHub> _hubContext;
    private readonly ILogger<FeedService> _logger;
    private readonly List<FeedItem> _items = [];
    private readonly HashSet<string> _fingerprints = [];
    private readonly Dictionary<string, (DateTimeOffset EmittedAt, decimal Score, string SignalType)> _lastEmittedBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _emissionHistoryBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SymbolSourceState> _sourceStateBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FeedItem> _pendingBroadcastBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourcePathMetrics> _sourcePathMetrics = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _lastRealInsertAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEmitSlot = DateTimeOffset.MinValue;
    private DateTimeOffset _nextBatchFlushAt = DateTimeOffset.MinValue;
    private bool _batchDispatchRunning;
    private long _broadcastCount;
    private long _suppressedCount;
    private long _deduplicatedCount;
    private long _staleDroppedCount;
    private long _totalBroadcastLatencyMs;
    private long _maxBroadcastLatencyMs;
    private long _sourceSwitchCount;

    public FeedService(IHubContext<FeedHub> hubContext, IConfiguration configuration, ILogger<FeedService> logger)
    {
        _hubContext = hubContext;
        var emitSpacingMs = Math.Clamp(configuration.GetValue<int?>("Feed:EmitSpacingMs") ?? 120, 30, 1000);
        _emitSpacing = TimeSpan.FromMilliseconds(emitSpacingMs);
        _liveFreshWindow = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Feed:LiveFreshSeconds") ?? 12, 2, 120));
        _pollingFreshWindow = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Feed:PollingFreshSeconds") ?? 20, 3, 180));
        _batchWindow = TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue<int?>("Feed:BatchWindowMs") ?? 120, 20, 1000));
        _maxBatchSize = Math.Clamp(configuration.GetValue<int?>("Feed:MaxBatchSize") ?? 50, 1, 500);
        _allowPollingWhileLiveFresh = configuration.GetValue<bool?>("Feed:AllowPollingWhileLiveFresh") ?? false;
        _logger = logger;
    }

    public IReadOnlyList<FeedItem> GetLatest(int limit = MaxItems)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var safeLimit = Math.Clamp(limit, 1, MaxItems);
            PruneExpiredItems(now);
            return _items
                .OrderByDescending(item => item.IsTopOpportunity)
                .ThenByDescending(item => GetDecayedScore(item, now))
                .ThenByDescending(item => item.Timestamp)
                .Take(safeLimit)
                .ToList();
        }
    }

    public bool IsRealDataStale(TimeSpan staleWindow)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return true;
            }

            return DateTimeOffset.UtcNow - _lastRealInsertAt > staleWindow;
        }
    }

    public IReadOnlyList<string> GetHotSymbols(int count)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredItems(now);
            return _items
                .Where(item => !string.IsNullOrWhiteSpace(item.Symbol))
                .OrderByDescending(item => item.IsTopOpportunity)
                .ThenByDescending(item => GetDecayedScore(item, now))
                .ThenByDescending(item => item.Timestamp)
                .Select(item => item.Symbol)
                .Distinct(StringComparer.Ordinal)
                .Take(Math.Clamp(count, 1, 100))
                .ToList();
        }
    }

    public Task AddSignalAsync(StockSignal signal, CancellationToken cancellationToken = default)
    {
        var feedItem = new FeedItem
        {
            Symbol = signal.Symbol,
            CountryCode = string.IsNullOrWhiteSpace(signal.CountryCode) ? "US" : signal.CountryCode.Trim().ToUpperInvariant(),
            Price = signal.Price,
            PriceRange = string.IsNullOrWhiteSpace(signal.PriceRange)
                ? PriceRangeResolver.GetPriceRange(signal.Price)
                : signal.PriceRange,
            ChangePercent = signal.ChangePercent,
            SignalType = signal.SignalType,
            Score = signal.Score > 0 ? signal.Score : signal.ActivityScore,
            ActivityScore = signal.ActivityScore,
            Confidence = signal.Confidence,
            TradeReadiness = string.IsNullOrWhiteSpace(signal.TradeReadiness) ? "WATCH" : signal.TradeReadiness,
            IsTopOpportunity = signal.IsTopOpportunity,
            IsTrending = signal.IsTrending,
            Headline = signal.Headline,
            Url = signal.Url,
            Reason = signal.SignalReason,
            Reasons = signal.Reasons ?? [],
            FloatShares = signal.FloatShares,
            InstitutionalOwnership = signal.InstitutionalOwnership,
            MarketCap = signal.MarketCap,
            Volume = signal.Volume,
            Flags = signal.Flags ?? [],
            VolumeRatio = signal.VolumeRatio,
            Momentum = signal.Momentum,
            Sentiment = NormalizeSentiment(signal.Sentiment),
            Acceleration = signal.Acceleration,
            GapPercent = signal.GapPercent,
            NewsCategory = signal.NewsCategory,
            RepeatCount = Math.Max(0, signal.RepeatCount),
            Timestamp = signal.Timestamp == default ? signal.ScannedAt : signal.Timestamp,
            MomentumDetectedAt = signal.MomentumDetectedAt,
            Source = string.IsNullOrWhiteSpace(signal.Source) ? "Scanner" : signal.Source
        };

        return AddItemAsync(feedItem, cancellationToken);
    }

    public Task AddNewsAsync(NormalizedNewsItem news, CancellationToken cancellationToken = default)
    {
        var type = news.Sentiment switch
        {
            "BULLISH" => "BULLISH",
            "BEARISH" => "BEARISH",
            _ => "NEWS"
        };
        var score = Math.Round(Math.Abs(news.SentimentScore) * 100m, 2);
        if (score < 70m)
        {
            return Task.CompletedTask;
        }

        var feedItem = new FeedItem
        {
            Symbol = news.Symbol,
            CountryCode = "US",
            SignalType = type,
            Score = score,
            ActivityScore = score,
            Confidence = NormalizeConfidence(string.Empty, score),
            TradeReadiness = "WATCH",
            IsTopOpportunity = false,
            IsTrending = false,
            Headline = news.Headline,
            Url = news.Url,
            Reason = $"News sentiment: {news.Sentiment} + Category: {news.Category} + Score {score:0.##}",
            Reasons = [$"News sentiment {news.Sentiment}", $"Category {news.Category}"],
            FloatShares = null,
            InstitutionalOwnership = null,
            MarketCap = null,
            Volume = null,
            Flags = [],
            Sentiment = NormalizeSentiment(news.Sentiment),
            NewsCategory = news.Category,
            RepeatCount = 1,
            Timestamp = news.Datetime == default ? DateTimeOffset.UtcNow : news.Datetime,
            MomentumDetectedAt = null,
            Source = string.IsNullOrWhiteSpace(news.Source) ? "NEWS" : news.Source
        };

        return AddItemAsync(feedItem, cancellationToken);
    }

    public async Task AddItemAsync(FeedItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.Symbol) || string.IsNullOrWhiteSpace(item.Headline))
        {
            return;
        }

        item.SignalType = NormalizeSignalType(item.SignalType);
        item.CountryCode = NormalizeCountryCode(item.CountryCode);
        item.Url = NormalizeUrl(item.Url);
        item.Score = item.Score > 0 ? item.Score : item.ActivityScore;
        item.PriceRange = string.IsNullOrWhiteSpace(item.PriceRange)
            ? PriceRangeResolver.GetPriceRange(item.Price)
            : item.PriceRange.Trim();
        item.Flags = item.Flags
            .Where(flag => !string.IsNullOrWhiteSpace(flag))
            .Select(flag => flag.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        item.Confidence = NormalizeConfidence(item.Confidence, item.Score);
        item.TradeReadiness = NormalizeTradeReadiness(item.TradeReadiness);
        item.Sentiment = NormalizeSentiment(item.Sentiment);
        item.NewsCategory = NormalizeNewsCategory(item.NewsCategory);
        item.RepeatCount = Math.Max(0, item.RepeatCount);
        var sourcePath = ResolveSourcePath(item.Source, item.SignalType);
        var fingerprint = BuildFingerprint(item);
        var wasInserted = false;
        var totalItems = 0;
        var shouldStartDispatcher = false;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredItems(now);

            if (!TryApplySourcePolicy(item, sourcePath, now, out var dropReason))
            {
                Interlocked.Increment(ref _staleDroppedCount);
                _logger.LogDebug(
                    "FeedService dropped {Symbol} from {SourcePath}. Reason={Reason}",
                    item.Symbol,
                    sourcePath,
                    dropReason);
                return;
            }

            if (sourcePath != FeedSourcePath.Live &&
                _lastEmittedBySymbol.TryGetValue(item.Symbol, out var lastEmission))
            {
                var withinQuietWindow = item.Timestamp - lastEmission.EmittedAt < SymbolReemitWindow;
                var scoreIncrease = item.Score - lastEmission.Score;
                var signalTypeChanged = !item.SignalType.Equals(lastEmission.SignalType, StringComparison.Ordinal);
                if (withinQuietWindow && scoreIncrease < SymbolScoreJumpThreshold && !signalTypeChanged)
                {
                    _logger.LogInformation(
                        "FeedService suppressed {Symbol} within {WindowSeconds}s (score +{ScoreIncrease:0.##}, signal type unchanged).",
                        item.Symbol,
                        SymbolReemitWindow.TotalSeconds,
                        scoreIncrease);
                    Interlocked.Increment(ref _suppressedCount);
                    return;
                }
            }

            if (!_fingerprints.Add(fingerprint))
            {
                _logger.LogInformation("FeedService deduplicated item for {Symbol} ({SignalType}).", item.Symbol, item.SignalType);
                Interlocked.Increment(ref _deduplicatedCount);
                return;
            }

            if (item.IsTopOpportunity)
            {
                foreach (var existing in _items)
                {
                    existing.IsTopOpportunity = false;
                }
            }

            item.IsTrending = UpdateTrending(item.Symbol, item.Timestamp);
            _items.Insert(0, item);
            _lastEmittedBySymbol[item.Symbol] = (item.Timestamp, item.Score, item.SignalType);
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }

            if (_fingerprints.Count > 1000)
            {
                _fingerprints.Clear();
                foreach (var existing in _items.Take(200))
                {
                    _fingerprints.Add(BuildFingerprint(existing));
                }
            }

            _lastRealInsertAt = DateTimeOffset.UtcNow;
            if (!_pendingBroadcastBySymbol.TryGetValue(item.Symbol, out var pendingExisting) ||
                item.Timestamp >= pendingExisting.Timestamp ||
                item.Score >= pendingExisting.Score)
            {
                _pendingBroadcastBySymbol[item.Symbol] = item;
            }

            if (!_batchDispatchRunning)
            {
                _batchDispatchRunning = true;
                _nextBatchFlushAt = now + _batchWindow;
                shouldStartDispatcher = true;
            }
            else if (_pendingBroadcastBySymbol.Count >= _maxBatchSize)
            {
                _nextBatchFlushAt = now;
            }

            totalItems = _items.Count;
            wasInserted = true;
        }

        if (!wasInserted)
        {
            return;
        }

        _logger.LogInformation(
            "FeedService added item: Symbol={Symbol}, Type={SignalType}, Source={Source}, Path={SourcePath}, TotalItems={TotalItems}.",
            item.Symbol,
            item.SignalType,
            item.Source,
            sourcePath,
            totalItems);

        if (shouldStartDispatcher)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessBroadcastQueueAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FeedService broadcast dispatcher failed.");
                    lock (_gate)
                    {
                        _batchDispatchRunning = false;
                    }
                }
            }, CancellationToken.None);
        }
    }

    private static string BuildFingerprint(FeedItem item)
    {
        var sourcePath = ResolveSourcePath(item.Source, item.SignalType);
        var bucketSeconds = sourcePath == FeedSourcePath.Live ? 2 : 30;
        var timeBucket = item.Timestamp.ToUnixTimeSeconds() / bucketSeconds;
        var quantizedPrice = Math.Round(item.Price, sourcePath == FeedSourcePath.Live ? 4 : 2);
        var quantizedChange = Math.Round(item.ChangePercent, 2);
        return $"{item.Symbol}|{item.SignalType}|{sourcePath}|{quantizedPrice}|{quantizedChange}|{timeBucket}";
    }

    private static FeedSourcePath ResolveSourcePath(string source, string signalType)
    {
        if (string.Equals(signalType, "NEWS", StringComparison.OrdinalIgnoreCase))
        {
            return FeedSourcePath.News;
        }

        var normalized = source?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Contains("TAPE", StringComparison.Ordinal) ||
            normalized.Contains("WS", StringComparison.Ordinal) ||
            normalized.Contains("WEBSOCKET", StringComparison.Ordinal))
        {
            return FeedSourcePath.Live;
        }

        if (normalized.Contains("SIM", StringComparison.Ordinal))
        {
            return FeedSourcePath.Simulation;
        }

        return FeedSourcePath.Polling;
    }

    private bool TryApplySourcePolicy(FeedItem item, FeedSourcePath sourcePath, DateTimeOffset now, out string reason)
    {
        reason = "accepted";
        if (!_sourceStateBySymbol.TryGetValue(item.Symbol, out var state))
        {
            state = new SymbolSourceState { Symbol = item.Symbol };
            _sourceStateBySymbol[item.Symbol] = state;
        }

        if (sourcePath == FeedSourcePath.Live)
        {
            if (state.LastWebsocketUpdateAt.HasValue && item.Timestamp < state.LastWebsocketUpdateAt.Value)
            {
                reason = "older_than_last_websocket";
                state.DroppedOutOfOrderCount++;
                return false;
            }

            state.LastWebsocketUpdateAt = item.Timestamp;
            state.LiveHealthy = true;
            return PromoteActiveSource(state, sourcePath, item.Timestamp, ref reason);
        }

        if (sourcePath == FeedSourcePath.Polling)
        {
            if (state.LastPollingUpdateAt.HasValue && item.Timestamp < state.LastPollingUpdateAt.Value)
            {
                reason = "older_than_last_polling";
                state.DroppedOutOfOrderCount++;
                return false;
            }

            if (state.LastWebsocketUpdateAt.HasValue)
            {
                var websocketAge = now - state.LastWebsocketUpdateAt.Value;
                var websocketIsFresh = websocketAge <= _liveFreshWindow;
                if (websocketIsFresh && !_allowPollingWhileLiveFresh)
                {
                    reason = "live_source_fresh";
                    state.DroppedStalePollingCount++;
                    return false;
                }

                if (item.Timestamp <= state.LastWebsocketUpdateAt.Value && !_allowPollingWhileLiveFresh)
                {
                    reason = "polling_older_than_websocket";
                    state.DroppedStalePollingCount++;
                    return false;
                }
            }

            state.LastPollingUpdateAt = item.Timestamp;
            state.PollingHealthy = true;
            return PromoteActiveSource(state, sourcePath, item.Timestamp, ref reason);
        }

        // NEWS/SIM updates should not force source ownership changes for market data.
        if (sourcePath == FeedSourcePath.News)
        {
            state.LastNewsUpdateAt = item.Timestamp;
        }
        else if (sourcePath == FeedSourcePath.Simulation)
        {
            state.LastSimulationUpdateAt = item.Timestamp;
        }

        return true;
    }

    private bool PromoteActiveSource(SymbolSourceState state, FeedSourcePath sourcePath, DateTimeOffset timestamp, ref string reason)
    {
        if (state.ActiveSource != sourcePath)
        {
            state.ActiveSource = sourcePath;
            state.LastSourceSwitchAt = timestamp;
            state.SourceSwitchCount++;
            Interlocked.Increment(ref _sourceSwitchCount);
            reason = "source_promoted";
        }

        return true;
    }

    private static string NormalizeSignalType(string signalType)
    {
        var normalized = signalType?.Trim().ToUpperInvariant() ?? "NEWS";
        return normalized switch
        {
            "SPIKE" or "BULLISH" or "BEARISH" or "NEWS" or "TRENDING" or "TOP_OPPORTUNITY" => normalized,
            _ => "NEWS"
        };
    }

    private static string NormalizeConfidence(string confidence, decimal score)
    {
        if (!string.IsNullOrWhiteSpace(confidence))
        {
            var normalized = confidence.Trim().ToUpperInvariant();
            if (normalized is "HIGH" or "MEDIUM" or "LOW")
            {
                return normalized;
            }
        }

        if (score > 100m)
        {
            return "HIGH";
        }

        return score > 70m ? "MEDIUM" : "LOW";
    }

    private static string NormalizeTradeReadiness(string tradeReadiness)
    {
        var normalized = tradeReadiness?.Trim().ToUpperInvariant();
        return normalized == "READY" ? "READY" : "WATCH";
    }

    private static string NormalizeCountryCode(string countryCode)
    {
        var normalized = countryCode?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "US" : normalized;
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return url.Trim();
    }

    private static string NormalizeSentiment(string sentiment)
    {
        var normalized = sentiment?.Trim().ToUpperInvariant();
        return normalized switch
        {
            "BULLISH" => "BULLISH",
            "BEARISH" => "BEARISH",
            _ => "NEUTRAL"
        };
    }

    private static string NormalizeNewsCategory(string category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : category.Trim();
    }

    private bool UpdateTrending(string symbol, DateTimeOffset timestamp)
    {
        if (!_emissionHistoryBySymbol.TryGetValue(symbol, out var history))
        {
            history = new Queue<DateTimeOffset>();
            _emissionHistoryBySymbol[symbol] = history;
        }

        history.Enqueue(timestamp);
        var cutoff = timestamp - TrendingWindow;
        while (history.Count > 0 && history.Peek() < cutoff)
        {
            history.Dequeue();
        }

        return history.Count >= TrendingThreshold;
    }

    private void PruneExpiredItems(DateTimeOffset now)
    {
        _items.RemoveAll(item => now - item.Timestamp > ExpireWindow);
    }

    private static decimal GetDecayedScore(FeedItem item, DateTimeOffset now)
    {
        var baseScore = item.Score > 0 ? item.Score : item.ActivityScore;
        var age = now - item.Timestamp;
        if (age <= ScoreDecayStart)
        {
            return baseScore;
        }

        var decayProgress = (age - ScoreDecayStart).TotalSeconds / Math.Max(1d, (ExpireWindow - ScoreDecayStart).TotalSeconds);
        var bounded = Math.Clamp((decimal)decayProgress, 0m, 1m);
        var decayFactor = 1m - (MaxScoreDecay * bounded);
        return Math.Round(baseScore * decayFactor, 2);
    }

    public FeedRuntimeDiagnostics GetRuntimeDiagnostics()
    {
        var broadcasts = Interlocked.Read(ref _broadcastCount);
        var totalLatency = Interlocked.Read(ref _totalBroadcastLatencyMs);
        var averageLatency = broadcasts > 0 ? totalLatency / (double)broadcasts : 0d;
        int pendingQueue;
        int trackedSymbols;
        Dictionary<string, SourcePathMetricsSnapshot> sourcePathMetrics;
        lock (_gate)
        {
            pendingQueue = _pendingBroadcastBySymbol.Count;
            trackedSymbols = _sourceStateBySymbol.Count;
            sourcePathMetrics = _sourcePathMetrics
                .ToDictionary(
                    pair => pair.Key,
                    pair => new SourcePathMetricsSnapshot
                    {
                        Count = pair.Value.Count,
                        AverageLatencyMs = pair.Value.Count > 0
                            ? Math.Round(pair.Value.TotalLatencyMs / (double)pair.Value.Count, 2)
                            : 0d,
                        MaxLatencyMs = pair.Value.MaxLatencyMs
                    },
                    StringComparer.Ordinal);
        }

        return new FeedRuntimeDiagnostics
        {
            BroadcastCount = broadcasts,
            SuppressedCount = Interlocked.Read(ref _suppressedCount),
            DeduplicatedCount = Interlocked.Read(ref _deduplicatedCount),
            StaleDroppedCount = Interlocked.Read(ref _staleDroppedCount),
            AverageBroadcastLatencyMs = Math.Round(averageLatency, 2),
            MaxBroadcastLatencyMs = Interlocked.Read(ref _maxBroadcastLatencyMs),
            EmitSpacingMs = (int)_emitSpacing.TotalMilliseconds,
            BatchWindowMs = (int)_batchWindow.TotalMilliseconds,
            MaxBatchSize = _maxBatchSize,
            PendingBroadcastQueue = pendingQueue,
            TrackedSymbols = trackedSymbols,
            SourceSwitchCount = Interlocked.Read(ref _sourceSwitchCount),
            SourcePathMetrics = sourcePathMetrics
        };
    }

    public IReadOnlyList<SymbolSourceDiagnostics> GetSourceDiagnostics(int maxSymbols = 100)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _sourceStateBySymbol.Values
                .OrderByDescending(state => state.LastSourceSwitchAt ?? DateTimeOffset.MinValue)
                .ThenBy(state => state.Symbol, StringComparer.Ordinal)
                .Take(Math.Clamp(maxSymbols, 1, 1000))
                .Select(state => new SymbolSourceDiagnostics
                {
                    Symbol = state.Symbol,
                    ActiveSource = state.ActiveSource.ToString().ToUpperInvariant(),
                    LastWebsocketUpdateAt = state.LastWebsocketUpdateAt,
                    LastPollingUpdateAt = state.LastPollingUpdateAt,
                    LastNewsUpdateAt = state.LastNewsUpdateAt,
                    LiveFreshAgeMs = state.LastWebsocketUpdateAt.HasValue
                        ? Math.Max(0, (long)(now - state.LastWebsocketUpdateAt.Value).TotalMilliseconds)
                        : null,
                    PollingFreshAgeMs = state.LastPollingUpdateAt.HasValue
                        ? Math.Max(0, (long)(now - state.LastPollingUpdateAt.Value).TotalMilliseconds)
                        : null,
                    LiveHealthy = state.LastWebsocketUpdateAt.HasValue &&
                                  now - state.LastWebsocketUpdateAt.Value <= _liveFreshWindow,
                    PollingHealthy = state.LastPollingUpdateAt.HasValue &&
                                     now - state.LastPollingUpdateAt.Value <= _pollingFreshWindow,
                    SourceSwitchCount = state.SourceSwitchCount,
                    LastSourceSwitchAt = state.LastSourceSwitchAt,
                    DroppedStalePollingCount = state.DroppedStalePollingCount,
                    DroppedOutOfOrderCount = state.DroppedOutOfOrderCount
                })
                .ToList();
        }
    }

    private void UpdateMaxBroadcastLatency(long latencyMs)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _maxBroadcastLatencyMs);
            if (latencyMs <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxBroadcastLatencyMs, latencyMs, current) == current)
            {
                return;
            }
        }
    }

    private async Task ProcessBroadcastQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            List<FeedItem> batch;
            TimeSpan delay;
            lock (_gate)
            {
                if (_pendingBroadcastBySymbol.Count == 0)
                {
                    _batchDispatchRunning = false;
                    _nextBatchFlushAt = DateTimeOffset.MinValue;
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var dueAt = _nextBatchFlushAt == DateTimeOffset.MinValue ? now : _nextBatchFlushAt;
                delay = dueAt > now ? dueAt - now : TimeSpan.Zero;
                if (delay > TimeSpan.Zero)
                {
                    batch = [];
                }
                else
                {
                    batch = _pendingBroadcastBySymbol.Values
                        .OrderByDescending(item => item.Timestamp)
                        .ThenByDescending(item => item.Score)
                        .Take(_maxBatchSize)
                        .ToList();

                    foreach (var item in batch)
                    {
                        _pendingBroadcastBySymbol.Remove(item.Symbol);
                    }

                    _nextBatchFlushAt = DateTimeOffset.UtcNow + _batchWindow;
                }
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                if (batch.Count == 1)
                {
                    var item = batch[0];
                    await _hubContext.Clients.All.SendAsync("newSignal", item, cancellationToken);
                    RecordBroadcastLatency(item, now);
                }
                else
                {
                    await _hubContext.Clients.All.SendAsync("newSignals", batch, cancellationToken);
                    foreach (var item in batch)
                    {
                        RecordBroadcastLatency(item, now);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SignalR batch broadcast failed for {Count} items.", batch.Count);
            }

            if (_emitSpacing > TimeSpan.Zero)
            {
                await Task.Delay(_emitSpacing, cancellationToken);
            }
        }
    }

    private void RecordBroadcastLatency(FeedItem item, DateTimeOffset now)
    {
        Interlocked.Increment(ref _broadcastCount);
        var latencyMs = Math.Max(0, (long)(now - item.Timestamp).TotalMilliseconds);
        Interlocked.Add(ref _totalBroadcastLatencyMs, latencyMs);
        UpdateMaxBroadcastLatency(latencyMs);

        var sourcePath = ResolveSourcePath(item.Source, item.SignalType).ToString().ToUpperInvariant();
        lock (_gate)
        {
            if (!_sourcePathMetrics.TryGetValue(sourcePath, out var metrics))
            {
                metrics = new SourcePathMetrics();
                _sourcePathMetrics[sourcePath] = metrics;
            }

            metrics.Count++;
            metrics.TotalLatencyMs += latencyMs;
            metrics.MaxLatencyMs = Math.Max(metrics.MaxLatencyMs, latencyMs);
        }
    }
}

public sealed class FeedRuntimeDiagnostics
{
    public long BroadcastCount { get; set; }

    public long SuppressedCount { get; set; }

    public long DeduplicatedCount { get; set; }

    public long StaleDroppedCount { get; set; }

    public double AverageBroadcastLatencyMs { get; set; }

    public long MaxBroadcastLatencyMs { get; set; }

    public int EmitSpacingMs { get; set; }

    public int BatchWindowMs { get; set; }

    public int MaxBatchSize { get; set; }

    public int PendingBroadcastQueue { get; set; }

    public int TrackedSymbols { get; set; }

    public long SourceSwitchCount { get; set; }

    public IReadOnlyDictionary<string, SourcePathMetricsSnapshot> SourcePathMetrics { get; set; } =
        new Dictionary<string, SourcePathMetricsSnapshot>(StringComparer.Ordinal);
}

public sealed class SymbolSourceDiagnostics
{
    public string Symbol { get; set; } = string.Empty;

    public string ActiveSource { get; set; } = "POLLING";

    public DateTimeOffset? LastWebsocketUpdateAt { get; set; }

    public DateTimeOffset? LastPollingUpdateAt { get; set; }

    public DateTimeOffset? LastNewsUpdateAt { get; set; }

    public long? LiveFreshAgeMs { get; set; }

    public long? PollingFreshAgeMs { get; set; }

    public bool LiveHealthy { get; set; }

    public bool PollingHealthy { get; set; }

    public int SourceSwitchCount { get; set; }

    public DateTimeOffset? LastSourceSwitchAt { get; set; }

    public long DroppedStalePollingCount { get; set; }

    public long DroppedOutOfOrderCount { get; set; }
}

public sealed class SourcePathMetricsSnapshot
{
    public long Count { get; set; }

    public double AverageLatencyMs { get; set; }

    public long MaxLatencyMs { get; set; }
}

internal sealed class SourcePathMetrics
{
    public long Count { get; set; }

    public long TotalLatencyMs { get; set; }

    public long MaxLatencyMs { get; set; }
}

internal sealed class SymbolSourceState
{
    public string Symbol { get; set; } = string.Empty;

    public FeedSourcePath ActiveSource { get; set; } = FeedSourcePath.Polling;

    public DateTimeOffset? LastWebsocketUpdateAt { get; set; }

    public DateTimeOffset? LastPollingUpdateAt { get; set; }

    public DateTimeOffset? LastNewsUpdateAt { get; set; }

    public DateTimeOffset? LastSimulationUpdateAt { get; set; }

    public bool LiveHealthy { get; set; }

    public bool PollingHealthy { get; set; }

    public int SourceSwitchCount { get; set; }

    public DateTimeOffset? LastSourceSwitchAt { get; set; }

    public long DroppedStalePollingCount { get; set; }

    public long DroppedOutOfOrderCount { get; set; }
}

internal enum FeedSourcePath
{
    Live = 0,
    Polling = 1,
    News = 2,
    Simulation = 3
}
