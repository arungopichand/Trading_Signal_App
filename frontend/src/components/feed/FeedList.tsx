import { Fragment, memo, useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { TopOpportunity } from "./TopOpportunity";
import type { FeedItem as FeedItemType } from "./types";

interface FeedListProps {
  items: FeedItemType[];
  newsItems?: FeedItemType[];
  nowMs: number;
  showTopOpportunity?: boolean;
  onRowSelect?: (item: FeedItemType) => void;
  pinnedSymbols: Set<string>;
  onPinToggle: (symbol: string) => void;
}

const SIGNAL_EXPIRY_SECONDS = 75;
type ViewMode = "compact" | "detailed";
type SortKey = "score" | "change" | "age" | "spike";
type SortDirection = "asc" | "desc";
type FlashDirection = "up" | "down";

function getAgeSeconds(item: FeedItemType, nowMs: number): number {
  const timestampMs = Date.parse(item.timestamp);
  if (Number.isNaN(timestampMs)) {
    return 0;
  }

  return Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
}

function normalizeSignal(item: FeedItemType): { label: string; colorClass: string } {
  if (item.signalType === "BEARISH" || item.changePercent < 0) {
    return { label: "SELL", colorClass: "text-red-400" };
  }

  if (item.signalType === "BULLISH" || item.signalType === "SPIKE" || item.changePercent >= 0) {
    return { label: "BUY", colorClass: "text-emerald-400" };
  }

  return { label: "NEUTRAL", colorClass: "text-slate-300" };
}

function formatAge(ageSeconds: number): string {
  if (ageSeconds < 60) {
    return `${ageSeconds}s`;
  }

  const minutes = Math.floor(ageSeconds / 60);
  const seconds = ageSeconds % 60;
  return `${minutes}m ${seconds}s`;
}

function formatSource(item: FeedItemType): string {
  const raw = (item.source || "").trim().toUpperCase();
  if (!raw) {
    return "Live";
  }

  if (raw.includes("WS") || raw.includes("WEBSOCKET") || raw.includes("STREAM")) {
    return "Live";
  }

  if (raw.includes("REST") || raw.includes("FALLBACK")) {
    return "Fallback";
  }

  if (raw.includes("CACHE")) {
    return "Delayed";
  }

  if (raw.includes("FINNHUB") || raw.includes("POLYGON")) {
    return "Fallback";
  }

  return raw;
}

function formatChange(changePercent: number): string {
  return `${changePercent >= 0 ? "+" : ""}${changePercent.toFixed(2)}%`;
}

function summarizeDetails(item: FeedItemType): string {
  const reasonParts: string[] = [];
  if (item.reason) {
    reasonParts.push(item.reason.trim());
  }

  if (item.reasons && item.reasons.length > 0) {
    reasonParts.push(...item.reasons.map((entry) => entry.trim()).filter(Boolean));
  }

  const summary = reasonParts.filter(Boolean).slice(0, 2).join(" | ");
  if (summary.length > 0) {
    return summary;
  }

  return item.headline;
}

function calculateSpikeScore(item: FeedItemType): number {
  const score = item.score ?? item.activityScore;
  const volumeSpike = Math.max(0, item.volumeRatio ?? (item.volume && item.volume >= 1_000_000 ? 1 : 0));
  return Number((Math.abs(item.changePercent) * 0.5 + volumeSpike * 0.3 + score * 0.2).toFixed(2));
}

function isNewsSpikeCandidate(item: FeedItemType): boolean {
  const score = item.score ?? item.activityScore;
  return Math.abs(item.changePercent) > 2 || score > 85;
}

function sortRows(rows: FeedItemType[], sortKey: SortKey, sortDirection: SortDirection, nowMs: number): FeedItemType[] {
  const sorted = [...rows].sort((a, b) => {
    let comparison = 0;
    if (sortKey === "score") {
      comparison = (a.score ?? a.activityScore) - (b.score ?? b.activityScore);
    } else if (sortKey === "spike") {
      comparison = calculateSpikeScore(a) - calculateSpikeScore(b);
    } else if (sortKey === "change") {
      comparison = a.changePercent - b.changePercent;
    } else {
      comparison = getAgeSeconds(a, nowMs) - getAgeSeconds(b, nowMs);
    }

    if (comparison === 0) {
      comparison = Date.parse(a.timestamp) - Date.parse(b.timestamp);
    }

    return sortDirection === "asc" ? comparison : -comparison;
  });

  return sorted;
}

export const FeedList = memo(function FeedList({
  items,
  newsItems = [],
  nowMs,
  showTopOpportunity = true,
  onRowSelect,
  pinnedSymbols,
  onPinToggle,
}: FeedListProps) {
  const [viewMode, setViewMode] = useState<ViewMode>("compact");
  const [sortKey, setSortKey] = useState<SortKey>("score");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [selectedRowId, setSelectedRowId] = useState<string | null>(null);
  const [sortLocked, setSortLocked] = useState(false);
  const [lockedOrder, setLockedOrder] = useState<string[]>([]);

  const prevScoreByIdRef = useRef<Map<string, number>>(new Map());
  const rowRefs = useRef<Record<string, HTMLTableRowElement | null>>({});

  const freshRows = useMemo(() => {
    return items.filter((item) => getAgeSeconds(item, nowMs) < SIGNAL_EXPIRY_SECONDS);
  }, [items, nowMs]);

  const topOpportunity = useMemo(
    () => freshRows.find((item) => item.isTopOpportunity) ?? freshRows[0],
    [freshRows],
  );

  const sortedRows = useMemo(() => sortRows(freshRows, sortKey, sortDirection, nowMs), [freshRows, sortKey, sortDirection, nowMs]);

  const orderedRows = useMemo(() => {
    if (!sortLocked || lockedOrder.length === 0) {
      return sortedRows;
    }

    const byId = new Map(sortedRows.map((row) => [row.id, row] as const));
    const preserved: FeedItemType[] = [];
    for (const id of lockedOrder) {
      const row = byId.get(id);
      if (row) {
        preserved.push(row);
        byId.delete(id);
      }
    }

    const newcomers = Array.from(byId.values());
    return [...preserved, ...newcomers];
  }, [sortedRows, sortLocked, lockedOrder]);

  const pinnedRows = useMemo(
    () => orderedRows.filter((row) => pinnedSymbols.has(row.symbol)),
    [orderedRows, pinnedSymbols],
  );
  const unpinnedRows = useMemo(
    () => orderedRows.filter((row) => !pinnedSymbols.has(row.symbol)),
    [orderedRows, pinnedSymbols],
  );
  const rows = useMemo(() => [...pinnedRows, ...unpinnedRows], [pinnedRows, unpinnedRows]);
  const topNewsBySymbol = useMemo(() => {
    const map = new Map<string, FeedItemType>();
    for (const item of newsItems) {
      if (item.signalType !== "NEWS" || !item.url || !item.headline) {
        continue;
      }

      const existing = map.get(item.symbol);
      if (!existing) {
        map.set(item.symbol, item);
        continue;
      }

      const existingScore = existing.score ?? existing.activityScore;
      const itemScore = item.score ?? item.activityScore;
      if (item.timestamp > existing.timestamp || itemScore > existingScore) {
        map.set(item.symbol, item);
      }
    }

    return map;
  }, [newsItems]);

  useEffect(() => {
    for (const row of rows) {
      const score = row.score ?? row.activityScore;
      const prev = prevScoreByIdRef.current.get(row.id);
      if (prev !== undefined && prev !== score) {
        const direction: FlashDirection = score > prev ? "up" : "down";
        const rowNode = rowRefs.current[row.id];
        if (rowNode) {
          const flashColor = direction === "up" ? "rgba(16, 185, 129, 0.14)" : "rgba(239, 68, 68, 0.14)";
          rowNode.animate(
            [{ backgroundColor: flashColor }, { backgroundColor: "transparent" }],
            { duration: 420, easing: "ease-out" },
          );
        }
      }

      prevScoreByIdRef.current.set(row.id, score);
    }
  }, [rows]);

  const activeRowId = selectedRowId ?? topOpportunity?.id ?? null;

  useEffect(() => {
    if (!activeRowId) {
      return;
    }

    const rowElement = rowRefs.current[activeRowId];
    if (!rowElement) {
      return;
    }

    rowElement.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [activeRowId]);

  useEffect(() => {
    if (!topOpportunity) {
      return;
    }

    onRowSelect?.(topOpportunity);
  }, [topOpportunity, onRowSelect]);

  const handleSort = (key: SortKey) => {
    setSortLocked(true);
    const nextDirection = sortKey === key ? (sortDirection === "asc" ? "desc" : "asc") : (key === "age" ? "asc" : "desc");

    if (sortKey === key) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDirection(nextDirection);
    }

    const nextRows = sortRows(freshRows, key, nextDirection, nowMs);
    setLockedOrder(nextRows.map((row) => row.id));
  };

  const handleKeyboardNavigation = (event: KeyboardEvent<HTMLDivElement>) => {
    if (rows.length === 0) {
      return;
    }

    const selectedIndex = activeRowId ? rows.findIndex((row) => row.id === activeRowId) : -1;
    if (event.key === "ArrowDown") {
      event.preventDefault();
      const nextIndex = selectedIndex < 0 ? 0 : Math.min(rows.length - 1, selectedIndex + 1);
      setSelectedRowId(rows[nextIndex].id);
      return;
    }

    if (event.key === "ArrowUp") {
      event.preventDefault();
      const nextIndex = selectedIndex <= 0 ? 0 : selectedIndex - 1;
      setSelectedRowId(rows[nextIndex].id);
      return;
    }

    if (event.key === "Enter") {
      event.preventDefault();
      const target = selectedIndex >= 0 ? rows[selectedIndex] : rows[0];
      setSelectedRowId(target.id);
      onRowSelect?.(target);
    }
  };

  const sortMarker = (key: SortKey) => {
    if (sortKey !== key) {
      return "";
    }

    return sortDirection === "asc" ? " (A)" : " (D)";
  };

  if (rows.length === 0) {
    return (
      <div className="px-4 py-6 text-sm text-slate-400">
        Monitoring market... waiting for high-probability setups
      </div>
    );
  }

  return (
    <section aria-live="polite" className="space-y-2 px-2 py-2">
      {showTopOpportunity ? (
        <TopOpportunity key={topOpportunity?.id ?? "no-top-opportunity"} item={topOpportunity} nowMs={nowMs} />
      ) : null}

      <div className="flex items-center justify-between px-1">
        <div className="text-[11px] font-semibold tracking-wide text-slate-400">Live Signals</div>
        <div className="inline-flex rounded border border-slate-700/80 bg-slate-900/60 p-0.5 text-[11px]">
          <button
            type="button"
            onClick={() => setViewMode("compact")}
            className={`px-2 py-1 ${viewMode === "compact" ? "bg-slate-700 text-white" : "text-slate-300"}`}
          >
            Compact
          </button>
          <button
            type="button"
            onClick={() => setViewMode("detailed")}
            className={`px-2 py-1 ${viewMode === "detailed" ? "bg-slate-700 text-white" : "text-slate-300"}`}
          >
            Detailed
          </button>
        </div>
      </div>

      <div
        className="overflow-x-auto rounded border border-slate-800/90 bg-slate-950/60"
        tabIndex={0}
        onKeyDown={handleKeyboardNavigation}
      >
        <table className="min-w-full table-fixed border-collapse text-xs text-slate-200">
          <thead className="sticky top-0 z-10 bg-slate-900/95 text-[11px] uppercase tracking-wide text-slate-400">
            <tr>
              <th className="px-2 py-2 text-left font-semibold">Symbol</th>
              <th className="px-2 py-2 text-left font-semibold">Signal</th>
              <th className="px-2 py-2 text-right font-semibold">
                <button type="button" className="hover:text-white" onClick={() => handleSort("score")}>
                  Score{sortMarker("score")}
                </button>
              </th>
              <th className="px-2 py-2 text-right font-semibold">
                <button type="button" className="hover:text-white" onClick={() => handleSort("spike")}>
                  Spike{sortMarker("spike")}
                </button>
              </th>
              <th className="px-2 py-2 text-right font-semibold">
                <button type="button" className="hover:text-white" onClick={() => handleSort("change")}>
                  Change{sortMarker("change")}
                </button>
              </th>
              <th className="px-2 py-2 text-right font-semibold">
                <button type="button" className="hover:text-white" onClick={() => handleSort("age")}>
                  Age{sortMarker("age")}
                </button>
              </th>
              <th className="px-2 py-2 text-left font-semibold">Source</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((item, index) => {
              const score = item.score ?? item.activityScore;
              const spikeScore = calculateSpikeScore(item);
              const ageSeconds = getAgeSeconds(item, nowMs);
              const signal = normalizeSignal(item);
              const pinned = pinnedSymbols.has(item.symbol);
              const topRowClass = index === 0 ? "bg-amber-500/10" : "";
              const selectedClass = activeRowId === item.id ? "bg-sky-500/10 ring-1 ring-sky-500/40" : "";
              const changeClass = item.changePercent >= 0 ? "text-emerald-400" : "text-red-400";
              const ageClass = ageSeconds <= 10
                ? "text-emerald-400"
                : ageSeconds <= 30
                  ? "text-amber-300"
                  : "text-red-400";

              return (
                <Fragment key={item.id}>
                  <tr
                    ref={(node) => {
                      rowRefs.current[item.id] = node;
                    }}
                    className={`cursor-pointer border-t border-slate-800/70 transition-colors duration-200 ${topRowClass} ${selectedClass} hover:bg-slate-800/30`}
                    onClick={() => {
                      setSelectedRowId(item.id);
                      onRowSelect?.(item);
                    }}
                  >
                    <td className="px-2 py-1.5 font-semibold text-slate-100">
                      <button
                        type="button"
                        className={`mr-1 text-[10px] ${pinned ? "text-amber-300" : "text-slate-600 hover:text-slate-400"}`}
                        onClick={(event) => {
                          event.stopPropagation();
                          onPinToggle(item.symbol);
                        }}
                        aria-label={pinned ? `Unpin ${item.symbol}` : `Pin ${item.symbol}`}
                      >
                        {pinned ? "PIN" : "PIN?"}
                      </button>
                      {item.symbol}
                    </td>
                    <td className={`px-2 py-1.5 font-semibold ${signal.colorClass}`}>{signal.label}</td>
                    <td className="px-2 py-1.5 text-right font-mono">{score.toFixed(1)}</td>
                    <td className="px-2 py-1.5 text-right font-mono text-sky-300">{spikeScore.toFixed(1)}</td>
                    <td className={`px-2 py-1.5 text-right font-mono ${changeClass}`}>{formatChange(item.changePercent)}</td>
                    <td className={`px-2 py-1.5 text-right font-mono ${ageClass}`}>{formatAge(ageSeconds)}</td>
                    <td className="px-2 py-1.5 text-slate-300">{formatSource(item)}</td>
                  </tr>
                  {viewMode === "detailed" ? (
                    <tr className={`border-t border-slate-800/40 ${topRowClass}`}>
                      <td colSpan={7} className="px-2 py-1.5 text-[11px] text-slate-400">
                        <span className="text-slate-300">{summarizeDetails(item)}</span>
                        {item.url ? (
                          <a
                            href={item.url}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="ml-2 text-slate-400 underline-offset-2 hover:text-slate-200 hover:underline"
                          >
                            link
                          </a>
                        ) : null}
                        {isNewsSpikeCandidate(item) ? (() => {
                          const topNews = topNewsBySymbol.get(item.symbol);
                          if (!topNews?.url) {
                            return null;
                          }

                          return (
                            <>
                              <span className="ml-2 text-amber-300">News:</span>
                              <a
                                href={topNews.url}
                                target="_blank"
                                rel="noopener noreferrer"
                                className="ml-1 text-amber-200 underline-offset-2 hover:text-amber-100 hover:underline"
                              >
                                {topNews.headline}
                              </a>
                            </>
                          );
                        })() : null}
                      </td>
                    </tr>
                  ) : null}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
});
