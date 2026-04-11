using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public sealed class SymbolUniverseService
{
    private readonly FinnhubService _finnhubService;
    private readonly ILogger<SymbolUniverseService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private List<string> _symbols = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public SymbolUniverseService(FinnhubService finnhubService, ILogger<SymbolUniverseService> logger)
    {
        _finnhubService = finnhubService;
        _logger = logger;
    }

    public DateTimeOffset LastRefresh => _lastRefresh;

    public async Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken = default)
    {
        if (_symbols.Count == 0)
        {
            await RefreshUniverseAsync(cancellationToken);
        }

        return _symbols;
    }

    public async Task RefreshUniverseAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var allSymbols = await _finnhubService.GetUsSymbolsAsync(cancellationToken);
            var filtered = allSymbols
                .Where(IsEligibleCommonStock)
                .Select(symbol => symbol.Symbol.Trim().ToUpperInvariant())
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(symbol => symbol, StringComparer.Ordinal)
                .ToList();

            if (filtered.Count == 0)
            {
                _logger.LogWarning("Symbol universe refresh returned zero eligible symbols. Keeping previous cache.");
                return;
            }

            _symbols = filtered;
            _lastRefresh = DateTimeOffset.UtcNow;
            _logger.LogInformation("Symbol universe refreshed with {Count} symbols.", _symbols.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetTopUniverseSliceAsync(int count, int offset, CancellationToken cancellationToken = default)
    {
        var symbols = await GetUniverseAsync(cancellationToken);
        if (symbols.Count == 0)
        {
            return [];
        }

        var safeCount = Math.Clamp(count, 1, 500);
        var safeOffset = Math.Max(0, offset % symbols.Count);
        if (safeOffset + safeCount <= symbols.Count)
        {
            return symbols
                .Skip(safeOffset)
                .Take(safeCount)
                .ToList();
        }

        var tail = symbols.Skip(safeOffset).ToList();
        var remaining = safeCount - tail.Count;
        if (remaining > 0)
        {
            tail.AddRange(symbols.Take(remaining));
        }

        return tail;
    }

    private static bool IsEligibleCommonStock(FinnhubSymbol symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Symbol))
        {
            return false;
        }

        var ticker = symbol.Symbol.Trim().ToUpperInvariant();
        if (ticker.Length > 5 || ticker.Contains('.'))
        {
            return false;
        }

        if (ticker.Any(character => character is < 'A' or > 'Z'))
        {
            return false;
        }

        var type = symbol.Type.Trim();
        if (!type.Equals("Common Stock", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("EQS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var description = symbol.Description.Trim();
        return !description.Contains("TEST", StringComparison.OrdinalIgnoreCase) &&
               !description.Contains("WARRANT", StringComparison.OrdinalIgnoreCase) &&
               !description.Contains("RIGHT", StringComparison.OrdinalIgnoreCase) &&
               !description.Contains("UNIT", StringComparison.OrdinalIgnoreCase);
    }
}
