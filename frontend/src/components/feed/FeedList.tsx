import { Fragment, memo, useMemo, useState } from "react";
import { TopOpportunity } from "./TopOpportunity";
import type { FeedItem as FeedItemType } from "./types";

interface FeedListProps {
  items: FeedItemType[];
  nowMs: number;
  showTopOpportunity?: boolean;
  onRowSelect?: (item: FeedItemType) => void;
}

const SIGNAL_EXPIRY_SECONDS = 75;

type ViewMode = "compact" | "detailed";
type SortKey = "score" | "change" | "age";
type SortDirection = "asc" | "desc";

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

  if (raw.includes("CACHE")) {
    return "Delayed";
  }

  if (raw.includes("REST") || raw.includes("FALLBACK") || raw.includes("FINNHUB") || raw.includes("POLYGON")) {
    return "Fallback";
  }

  if (raw === "WS" || raw.includes("WEBSOCKET") || raw.includes("STREAM")) {
    return "Live";
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

export const FeedList = memo(function FeedList({
  items,
  nowMs,
  showTopOpportunity = true,
  onRowSelect,
}: FeedListProps) {
  const [viewMode, setViewMode] = useState<ViewMode>("compact");
  const [sortKey, setSortKey] = useState<SortKey>("score");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [selectedRowId, setSelectedRowId] = useState<string | null>(null);

  const freshSorted = useMemo(() => {
    const fresh = items.filter((item) => getAgeSeconds(item, nowMs) < SIGNAL_EXPIRY_SECONDS);
    return [...fresh];
  }, [items, nowMs]);

  const topOpportunity = freshSorted.find((item) => item.isTopOpportunity);
  const rowsBase = topOpportunity ? freshSorted.filter((item) => item.id !== topOpportunity.id) : freshSorted;

  const rows = useMemo(() => {
    const sorted = [...rowsBase].sort((a, b) => {
      let comparison = 0;
      if (sortKey === "score") {
        comparison = (a.score ?? a.activityScore) - (b.score ?? b.activityScore);
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
  }, [rowsBase, sortKey, sortDirection, nowMs]);

  const handleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDirection((prev) => (prev === "asc" ? "desc" : "asc"));
      return;
    }

    setSortKey(key);
    setSortDirection(key === "age" ? "asc" : "desc");
  };

  const sortMarker = (key: SortKey) => {
    if (sortKey !== key) {
      return "";
    }

    return sortDirection === "asc" ? " ↑" : " ↓";
  };

  if (freshSorted.length === 0) {
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

      <div className="overflow-x-auto rounded border border-slate-800/90 bg-slate-950/60">
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
              const ageSeconds = getAgeSeconds(item, nowMs);
              const signal = normalizeSignal(item);
              const topRowClass = index === 0 ? "bg-amber-500/10" : "";
              const changeClass = item.changePercent >= 0 ? "text-emerald-400" : "text-red-400";
              const ageClass = ageSeconds <= 10
                ? "text-emerald-400"
                : ageSeconds <= 30
                  ? "text-amber-300"
                  : "text-red-400";
              const selectedClass = selectedRowId === item.id ? "bg-sky-500/10 ring-1 ring-sky-500/40" : "";

              return (
                <Fragment key={item.id}>
                  <tr
                    className={`cursor-pointer border-t border-slate-800/70 transition-colors duration-150 ${topRowClass} ${selectedClass} hover:bg-slate-800/30`}
                    onClick={() => {
                      setSelectedRowId(item.id);
                      onRowSelect?.(item);
                    }}
                  >
                    <td className="px-2 py-1.5 font-semibold text-slate-100">{item.symbol}</td>
                    <td className={`px-2 py-1.5 font-semibold ${signal.colorClass}`}>{signal.label}</td>
                    <td className="px-2 py-1.5 text-right font-mono">{score.toFixed(1)}</td>
                    <td className={`px-2 py-1.5 text-right font-mono ${changeClass}`}>{formatChange(item.changePercent)}</td>
                    <td className={`px-2 py-1.5 text-right font-mono ${ageClass}`}>{formatAge(ageSeconds)}</td>
                    <td className="px-2 py-1.5 text-slate-300">{formatSource(item)}</td>
                  </tr>
                  {viewMode === "detailed" ? (
                    <tr className={`border-t border-slate-800/40 ${topRowClass}`}>
                      <td colSpan={6} className="px-2 py-1.5 text-[11px] text-slate-400">
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
