using SignalFeed.Api.Models;

namespace SignalFeed.Api.Services;

public class SignalEngine
{
    private readonly FinnhubService _finnhub;
    private readonly SymbolUniverseService _symbolUniverseService;

    public SignalEngine(FinnhubService finnhub, SymbolUniverseService symbolUniverseService)
    {
        _finnhub = finnhub;
        _symbolUniverseService = symbolUniverseService;
    }

    public async Task<ScanBatchResult> GenerateSignalsAsync(CancellationToken cancellationToken = default)
    {
        var signals = new List<StockSignal>();
        var snapshots = new List<QuoteSnapshot>();
        var scannedAt = DateTimeOffset.UtcNow;
        var trackedSymbols = await _symbolUniverseService.GetActiveSymbolsAsync(cancellationToken);

        if (trackedSymbols.Count == 0)
        {
            return new ScanBatchResult
            {
                Snapshots = [],
                Signals = []
            };
        }

        foreach (var trackedSymbol in trackedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var quote = await _finnhub.GetQuoteAsync(trackedSymbol.Symbol);

            if (quote == null || quote.PreviousClose <= 0)
            {
                continue;
            }

            var changePercent =
                Math.Round(((quote.CurrentPrice - quote.PreviousClose) / quote.PreviousClose) * 100m, 2);

            snapshots.Add(new QuoteSnapshot
            {
                Symbol = trackedSymbol.Symbol,
                CurrentPrice = Math.Round(quote.CurrentPrice, 2),
                PreviousClose = Math.Round(quote.PreviousClose, 2),
                DayHigh = Math.Round(quote.High, 2),
                DayLow = Math.Round(quote.Low, 2),
                ChangePercent = changePercent,
                ScannedAt = scannedAt
            });

            var signalType = GetSignalType(quote, changePercent, out var signalReason, out var breakoutBonus);

            if (signalType is null)
            {
                continue;
            }

            var activityScore = Math.Round(Math.Abs(changePercent) + breakoutBonus, 2);

            signals.Add(new StockSignal
            {
                Symbol = trackedSymbol.Symbol,
                Price = Math.Round(quote.CurrentPrice, 2),
                ChangePercent = changePercent,
                SignalType = signalType,
                ActivityScore = activityScore,
                Headline = BuildHeadline(signalType, trackedSymbol.Symbol, quote),
                SignalReason = signalReason,
                ScannedAt = scannedAt
            });
        }

        return new ScanBatchResult
        {
            Snapshots = snapshots,
            Signals = signals
                .OrderByDescending(signal => signal.ActivityScore)
                .Take(20)
                .ToList()
        };
    }

    private static string? GetSignalType(
        QuoteResponse quote,
        decimal changePercent,
        out string signalReason,
        out decimal breakoutBonus)
    {
        signalReason = string.Empty;
        breakoutBonus = 0m;

        if (quote.High > 0 && quote.CurrentPrice >= quote.High)
        {
            signalReason = "NHOD BREAKOUT";
            breakoutBonus = 2.5m;
            return "NHOD BREAKOUT";
        }

        if (changePercent > 3m)
        {
            signalReason = "MOMENTUM SPIKE";
            return "STRONG BULLISH";
        }

        if (changePercent > 1m)
        {
            signalReason = "BULLISH MOVE";
            return "BULLISH";
        }

        if (changePercent < -3m)
        {
            signalReason = "BEARISH MOVE";
            return "STRONG BEARISH";
        }

        if (changePercent < -1m)
        {
            signalReason = "BEARISH MOVE";
            return "BEARISH";
        }

        return null;
    }

    private static string BuildHeadline(string signalType, string symbol, QuoteResponse quote)
    {
        return signalType switch
        {
            "NHOD BREAKOUT" => $"{symbol} is pressing the day high at ${quote.High:F2}",
            "STRONG BULLISH" => $"{symbol} is showing a momentum spike above 3%",
            "BULLISH" => $"{symbol} is trading more than 1% above the previous close",
            "STRONG BEARISH" => $"{symbol} is down more than 3% on the session",
            "BEARISH" => $"{symbol} is trading more than 1% below the previous close",
            _ => "Scanner signal"
        };
    }
}
