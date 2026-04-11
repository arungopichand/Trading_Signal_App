import { AnimatePresence, motion } from "framer-motion";
import { useCallback, useEffect, useMemo, useState } from "react";
import { API, SIGNALS_URL } from "./config/api";
import "./App.css";

interface StockSignal {
  symbol: string;
  price: number;
  changePercent: number;
  signalType: string;
  activityScore: number;
  headline: string;
  signalReason: string;
  scannedAt: string;
}

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const percentFormatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
  signDisplay: "always",
});

const timeFormatter = new Intl.DateTimeFormat("en-US", {
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit",
});

function normalizeSignal(value: unknown): StockSignal | null {
  if (!value || typeof value !== "object") {
    return null;
  }

  const candidate = value as Record<string, unknown>;
  const symbol = typeof candidate.symbol === "string" ? candidate.symbol : "";
  const signalType = typeof candidate.signalType === "string" ? candidate.signalType : "";
  const signalReason = typeof candidate.signalReason === "string" ? candidate.signalReason : "";
  const headline = typeof candidate.headline === "string" ? candidate.headline : "";
  const scannedAt = typeof candidate.scannedAt === "string" ? candidate.scannedAt : "";
  const price = Number(candidate.price);
  const changePercent = Number(candidate.changePercent);
  const activityScore = Number(candidate.activityScore);

  if (
    !symbol ||
    !signalType ||
    Number.isNaN(price) ||
    Number.isNaN(changePercent) ||
    Number.isNaN(activityScore)
  ) {
    return null;
  }

  return {
    symbol,
    signalType,
    signalReason,
    headline,
    scannedAt,
    price,
    changePercent,
    activityScore,
  };
}

function sortSignals(signals: StockSignal[]): StockSignal[] {
  return [...signals].sort((left, right) => right.activityScore - left.activityScore);
}

function isHotSignal(signal: StockSignal): boolean {
  return signal.signalType.includes("NHOD") || signal.signalType.includes("STRONG");
}

function App() {
  const [signals, setSignals] = useState<StockSignal[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const fetchSignals = useCallback(async (abortSignal?: AbortSignal) => {
    if (!API) {
      setSignals([]);
      setError("VITE_API_BASE_URL is not configured.");
      setIsLoading(false);
      return;
    }

    try {
      const response = await fetch(SIGNALS_URL, {
        signal: abortSignal,
        headers: { Accept: "application/json" },
        cache: "no-store",
      });

      if (!response.ok) {
        throw new Error(`Scanner API returned ${response.status}.`);
      }

      const payload: unknown = await response.json();

      if (!Array.isArray(payload)) {
        throw new Error("Scanner API returned an invalid payload.");
      }

      const nextSignals = sortSignals(payload.map(normalizeSignal).filter(Boolean) as StockSignal[]);

      setSignals(nextSignals);
      setError(null);
      setLastUpdated(new Date());
    } catch (fetchError) {
      if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
        return;
      }

      setSignals([]);
      setError(fetchError instanceof Error ? fetchError.message : "Unable to load signals.");
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    let activeController: AbortController | null = null;

    const poll = () => {
      activeController?.abort();
      activeController = new AbortController();
      void fetchSignals(activeController.signal);
    };

    poll();
    const intervalId = window.setInterval(poll, 8000);

    return () => {
      window.clearInterval(intervalId);
      activeController?.abort();
    };
  }, [fetchSignals]);

  const summary = useMemo(() => {
    const hotCount = signals.filter(isHotSignal).length;
    const bullishCount = signals.filter((signal) => signal.changePercent > 0).length;
    const bearishCount = signals.filter((signal) => signal.changePercent < 0).length;
    const strongest = signals[0];

    return {
      hotCount,
      bullishCount,
      bearishCount,
      strongestMove: strongest
        ? `${strongest.symbol} | score ${strongest.activityScore.toFixed(2)}`
        : "Waiting for activity",
    };
  }, [signals]);

  return (
    <main className="scanner-app">
      <section className="scanner-shell">
        <header className="scanner-header">
          <div>
            <div className="scanner-kicker">
              <span className="live-dot" />
              Free-tier-safe scanner
            </div>
            <h1 className="scanner-title">Market Rotation Dashboard</h1>
            <p className="scanner-subtitle">
              Rotating tracked symbols, activity scoring, and rate-limit-safe polling from the backend.
            </p>
          </div>

          <div className="scanner-status-panel">
            <div className="status-label">Feed</div>
            <div className="status-value">{error ? "Degraded" : "Streaming"}</div>
            <div className="status-meta">
              {lastUpdated ? `Updated ${lastUpdated.toLocaleTimeString()}` : "Waiting for first scan"}
            </div>
          </div>
        </header>

        <section className="summary-grid" aria-label="scanner summary">
          <motion.article layout className="summary-tile">
            <span className="summary-label">Current Signals</span>
            <strong className="summary-value">{signals.length}</strong>
          </motion.article>

          <motion.article layout className="summary-tile">
            <span className="summary-label">Hot Alerts</span>
            <strong className="summary-value">{summary.hotCount}</strong>
          </motion.article>

          <motion.article layout className="summary-tile">
            <span className="summary-label">Bull vs Bear</span>
            <strong className="summary-value">
              {summary.bullishCount} / {summary.bearishCount}
            </strong>
          </motion.article>

          <motion.article layout className="summary-tile summary-tile-wide">
            <span className="summary-label">Top Rank</span>
            <strong className="summary-value">{summary.strongestMove}</strong>
          </motion.article>
        </section>

        {error ? (
          <div className="message-banner error-banner" role="alert">
            {error}
          </div>
        ) : null}

        <section className="scanner-board" aria-live="polite">
          <div className="scanner-board-header">
            <span>Symbol</span>
            <span>Signal</span>
            <span>Price</span>
            <span>% Change</span>
            <span>Reason</span>
            <span>Updated</span>
          </div>

          {isLoading ? (
            <div className="empty-state">Loading scanner feed...</div>
          ) : signals.length === 0 ? (
            <div className="empty-state">
              No qualified signals right now. The scanner is still running and safely backing off under light market activity or API limits.
            </div>
          ) : (
            <AnimatePresence initial={false}>
              {signals.map((signal) => {
                const positive = signal.changePercent >= 0;
                const hot = isHotSignal(signal);

                return (
                  <motion.article
                    layout
                    key={`${signal.symbol}-${signal.signalType}-${signal.scannedAt}`}
                    initial={{ opacity: 0, y: 12 }}
                    animate={{ opacity: 1, y: 0 }}
                    exit={{ opacity: 0, y: -12 }}
                    transition={{ duration: 0.2 }}
                    className={`signal-row ${positive ? "signal-row-positive" : "signal-row-negative"}`}
                  >
                    <div className="signal-symbol-block">
                      <span className="signal-symbol">{signal.symbol}</span>
                      {hot ? <span className="hot-badge">{"\u{1F6A8} HOT"}</span> : null}
                    </div>

                    <div className="signal-type-cell">
                      <span className={`signal-chip ${hot ? "signal-chip-hot" : ""}`}>
                        {signal.signalType}
                      </span>
                    </div>

                    <div className="signal-price">{currencyFormatter.format(signal.price)}</div>

                    <div
                      className={`signal-change ${
                        positive ? "signal-change-positive" : "signal-change-negative"
                      }`}
                    >
                      {percentFormatter.format(signal.changePercent)}%
                    </div>

                    <div className="signal-headline">
                      <strong>{signal.signalReason}</strong>
                      <span>{signal.headline}</span>
                    </div>

                    <div className="signal-updated">
                      <span className="signal-score">Score {signal.activityScore.toFixed(2)}</span>
                      <span>{signal.scannedAt ? timeFormatter.format(new Date(signal.scannedAt)) : "Just now"}</span>
                    </div>
                  </motion.article>
                );
              })}
            </AnimatePresence>
          )}
        </section>
      </section>
    </main>
  );
}

export default App;
