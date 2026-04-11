import type { FeedItem } from "./types";

interface FeedRowProps {
  item: FeedItem;
  nowMs: number;
}

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
});

const signalStyle: Record<FeedItem["signalType"], string> = {
  SPIKE: "bg-yellow-400/30 text-yellow-200 font-bold",
  BULLISH: "bg-emerald-500/20 text-emerald-300",
  BEARISH: "bg-red-500/20 text-red-300",
  NEWS: "bg-sky-500/20 text-sky-300",
};

const signalLabel: Record<FeedItem["signalType"], string> = {
  SPIKE: "\u{1F525} SPIKE",
  BULLISH: "\u{1F4C8} BULLISH",
  BEARISH: "\u{1F4C9} BEARISH",
  NEWS: "\u{1F4F0} NEWS",
};

const confidenceStyle: Record<"HIGH" | "MEDIUM" | "LOW", string> = {
  HIGH: "text-lime-200",
  MEDIUM: "text-slate-100",
  LOW: "text-slate-400",
};

function formatRelativeTime(timestamp: string, nowMs: number): string {
  const timeMs = Date.parse(timestamp);
  if (Number.isNaN(timeMs)) {
    return "--";
  }

  const seconds = Math.max(0, Math.floor((nowMs - timeMs) / 1000));
  if (seconds < 60) {
    return `${seconds}s ago`;
  }

  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ago`;
}

export function FeedRow({ item, nowMs }: FeedRowProps) {
  const score = item.score ?? item.activityScore;
  const isUp = item.changePercent >= 0;
  const isStrong = score > 90;
  const isTopOpportunity = item.isTopOpportunity === true;
  const isTrending = item.isTrending === true;
  const confidence = item.confidence ?? "LOW";
  const symbolClass = isUp ? "text-emerald-300" : "text-red-300";
  const topClass = isTopOpportunity ? "feed-top-opportunity" : "";
  const timestampMs = Date.parse(item.timestamp);
  const ageSeconds = Number.isNaN(timestampMs) ? 0 : Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
  const timeDecayClass = ageSeconds > 30 ? "feed-age-stale" : ageSeconds > 10 ? "feed-age-aging" : "";
  const strengthClass = score > 90 ? "feed-strength-strong" : score >= 70 ? "feed-strength-medium" : "feed-strength-weak";

  return (
    <article className={`grid animate-feed-in grid-cols-[130px_100px_92px_180px_minmax(0,1fr)_92px_92px] gap-2 border-b border-slate-900 px-4 py-2 text-sm text-slate-200 transition-colors hover:bg-slate-800/45 ${isStrong ? "feed-strong-signal" : ""} ${topClass} ${timeDecayClass} ${strengthClass}`}>
      <span className={`truncate font-bold ${symbolClass}`}>{item.symbol}</span>
      <span className="font-mono">{item.price > 0 ? currencyFormatter.format(item.price) : "-"}</span>
      <span className={`font-mono font-semibold ${isUp ? "text-emerald-300" : "text-red-300"}`}>
        {item.changePercent > 0 ? "+" : ""}
        {item.changePercent.toFixed(2)}%
      </span>
      <span className="inline-flex items-center gap-1">
        <span className={`inline-flex w-fit px-2 py-0.5 text-[11px] font-semibold ${signalStyle[item.signalType]}`}>
          {signalLabel[item.signalType]}
          <span className={confidenceStyle[confidence]}>({confidence})</span>
        </span>
        {isTopOpportunity ? (
          <span className="inline-flex w-fit px-2 py-0.5 text-[11px] font-bold text-amber-100">
            {"\u{1F525} TOP OPPORTUNITY"}
          </span>
        ) : null}
        {isTrending ? (
          <span className="inline-flex w-fit px-2 py-0.5 text-[11px] font-bold text-orange-200">
            {"\u{1F525} TRENDING"}
          </span>
        ) : null}
      </span>
      <span className="truncate text-slate-200">{item.headline}</span>
      <span className="font-mono text-slate-400">
        {item.timestamp ? formatRelativeTime(item.timestamp, nowMs) : "--"}
      </span>
      <span className="truncate text-slate-400">{item.source || "Scanner"}</span>
    </article>
  );
}
