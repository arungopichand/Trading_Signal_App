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
  TRENDING: "bg-orange-500/20 text-orange-200",
  TOP_OPPORTUNITY: "bg-amber-400/25 text-amber-100 font-bold",
};

const signalLabel: Record<FeedItem["signalType"], string> = {
  SPIKE: "\u{1F525} SPIKE",
  BULLISH: "\u{1F4C8} BULLISH",
  BEARISH: "\u{1F4C9} BEARISH",
  NEWS: "\u{1F4F0} NEWS",
  TRENDING: "\u{1F525} TRENDING",
  TOP_OPPORTUNITY: "\u{1F525} TOP_OPPORTUNITY",
};

const confidenceStyle: Record<"HIGH" | "MEDIUM" | "LOW", string> = {
  HIGH: "text-lime-200",
  MEDIUM: "text-slate-100",
  LOW: "text-slate-400",
};

const readinessStyle: Record<"READY" | "WATCH", string> = {
  READY: "bg-lime-500/20 text-lime-200",
  WATCH: "bg-slate-700/60 text-slate-300",
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

function formatTime(timestamp: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) {
    return "--:--:-- --";
  }

  return date.toLocaleTimeString("en-US", {
    hour12: true,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function formatMomentumTime(timestamp?: string): string {
  if (!timestamp) {
    return "";
  }

  return formatTime(timestamp);
}

function formatCompactMillions(value?: number): string {
  if (typeof value !== "number" || Number.isNaN(value) || value <= 0) {
    return "-";
  }

  if (value >= 1000) {
    return `${(value / 1000).toFixed(1)}B`;
  }

  return `${value.toFixed(0)}M`;
}

function formatSignalClock(timestamp: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) {
    return "--:--";
  }

  return date.toLocaleTimeString("en-US", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatVolumeCompact(volume?: number): string {
  if (typeof volume !== "number" || Number.isNaN(volume) || volume <= 0) {
    return "-";
  }

  if (volume >= 1_000_000) {
    return `${(volume / 1_000_000).toFixed(1)}m`;
  }

  if (volume >= 1_000) {
    return `${Math.round(volume / 1_000)}k`;
  }

  return `${Math.round(volume)}`;
}

function getFlagEmoji(countryCode: string) {
  if (!countryCode) {
    return "\u{1F1FA}\u{1F1F8}";
  }

  const cc = countryCode.toUpperCase();
  return cc.replace(/./g, (char) => String.fromCodePoint(127397 + char.charCodeAt(0)));
}

function buildTopFactors(item: FeedItem): string[] {
  if (item.signalType === "SPIKE") {
    const factors: string[] = [];
    if (typeof item.volumeRatio === "number" && item.volumeRatio > 0) {
      factors.push(`Volume: ${item.volumeRatio.toFixed(1)}x`);
    }

    if (typeof item.momentum === "number") {
      factors.push(`Momentum: ${item.momentum >= 0 ? "+" : ""}${item.momentum.toFixed(1)}%`);
    }

    const momentumClock = formatMomentumTime(item.momentumDetectedAt);
    if (momentumClock) {
      factors.push(`Momentum: ${momentumClock}`);
    }

    return factors.slice(0, 3);
  }

  if (item.signalType === "NEWS") {
    const factors: string[] = [];
    if (item.sentiment && item.sentiment !== "NEUTRAL") {
      const normalized = item.sentiment === "BULLISH" ? "Bullish" : "Bearish";
      factors.push(`Sentiment: ${normalized}`);
    }

    if (item.newsCategory) {
      factors.push(`Category: ${item.newsCategory}`);
    }

    return factors.slice(0, 3);
  }

  if (item.signalType === "TRENDING") {
    const count = typeof item.repeatCount === "number" ? item.repeatCount : 0;
    return count > 0 ? [`Repeats: ${count}`] : [];
  }

  const fallback: Array<{ rank: number; text: string }> = [];
  if (typeof item.momentum === "number") {
    fallback.push({
      rank: Math.abs(item.momentum) + 1.2,
      text: `Momentum: ${item.momentum >= 0 ? "+" : ""}${item.momentum.toFixed(1)}%`,
    });
  }

  if (typeof item.volumeRatio === "number" && item.volumeRatio > 0) {
    fallback.push({
      rank: Math.abs(item.volumeRatio - 1) + 1.1,
      text: `Volume: ${item.volumeRatio.toFixed(1)}x`,
    });
  }

  if (item.sentiment && item.sentiment !== "NEUTRAL") {
    const normalized = item.sentiment === "BULLISH" ? "Bullish" : "Bearish";
    fallback.push({
      rank: 1.0,
      text: `Sentiment: ${normalized}`,
    });
  }

  return fallback
    .sort((a, b) => b.rank - a.rank)
    .slice(0, 3)
    .map((factor) => factor.text);
}

export function FeedRow({ item, nowMs }: FeedRowProps) {
  const score = item.score ?? item.activityScore;
  const isUp = item.changePercent >= 0;
  const isStrong = score > 90;
  const isTopOpportunity = item.isTopOpportunity === true;
  const confidence = item.confidence ?? "LOW";
  const tradeReadiness = item.tradeReadiness ?? "WATCH";
  const symbolClass = isUp ? "text-emerald-300" : "text-red-300";
  const topClass = isTopOpportunity ? "feed-top-opportunity" : "";
  const timestampMs = Date.parse(item.timestamp);
  const ageSeconds = Number.isNaN(timestampMs) ? 0 : Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
  const timeDecayClass = ageSeconds > 30 ? "feed-age-stale" : ageSeconds > 10 ? "feed-age-aging" : "";
  const strengthClass = score > 90 ? "feed-strength-strong" : score >= 70 ? "feed-strength-medium" : "feed-strength-weak";
  const countryFlag = getFlagEmoji(item.countryCode || "US");
  const topFactors = buildTopFactors(item);
  const advancedFactors = [
    `Float: ${formatCompactMillions(item.floatShares)}`,
    `IO: ${typeof item.institutionalOwnership === "number" ? `${item.institutionalOwnership.toFixed(1)}%` : "-"}`,
    `MC: ${formatCompactMillions(item.marketCap)}`,
  ];
  const flags = (item.flags ?? []).slice(0, 4);
  const compactSummary = `${formatSignalClock(item.timestamp)} ${item.changePercent > 0 ? "+" : ""}${item.changePercent.toFixed(1)}% ${formatVolumeCompact(item.volume)} vol`;

  return (
    <article className={`grid grid-cols-[auto_auto_auto_auto_1fr] items-center gap-2 border-b border-slate-900 px-4 py-2 text-sm text-slate-200 transition-colors hover:bg-slate-800/45 md:grid-cols-[auto_auto_auto_auto_1fr_auto] ${isStrong ? "feed-strong-signal" : ""} ${topClass} ${timeDecayClass} ${strengthClass}`}>
      <span className={`w-[172px] overflow-hidden text-ellipsis whitespace-nowrap font-bold ${symbolClass}`}>
        <span className="mr-1">{countryFlag}</span>
        {item.symbol}
        {item.priceRange ? <span className="ml-1 text-slate-400">{item.priceRange}</span> : null}
      </span>

      <span className="w-[92px] overflow-hidden text-ellipsis whitespace-nowrap font-mono">
        {item.price > 0 ? currencyFormatter.format(item.price) : "-"}
      </span>

      <span className={`w-[84px] overflow-hidden text-ellipsis whitespace-nowrap font-mono font-semibold ${isUp ? "text-emerald-300" : "text-red-300"}`}>
        {item.changePercent > 0 ? "+" : ""}
        {item.changePercent.toFixed(2)}%
      </span>

      <span className="w-[180px] overflow-hidden text-ellipsis whitespace-nowrap">
        <span className={`inline-flex w-fit max-w-full items-center truncate px-2 py-0.5 text-[11px] font-semibold ${signalStyle[item.signalType]}`}>
          {signalLabel[item.signalType]}
          <span className={confidenceStyle[confidence]}>({confidence})</span>
        </span>
        <span className={`ml-1 inline-flex w-fit px-2 py-0.5 text-[11px] font-bold ${readinessStyle[tradeReadiness]}`}>
          {tradeReadiness}
        </span>
      </span>

      <span className="min-w-0 max-w-[400px] overflow-hidden">
        {item.url ? (
          <a
            href={item.url}
            target="_blank"
            rel="noopener noreferrer"
            className="block truncate text-slate-200 underline-offset-2 hover:underline"
            title={item.headline}
          >
            {item.headline}
          </a>
        ) : (
          <span className="block truncate text-slate-200" title={item.headline}>{item.headline}</span>
        )}
        {topFactors.length > 0 ? (
          <div className="flex gap-2 overflow-hidden text-ellipsis text-xs text-slate-400 max-md:flex-nowrap md:flex-wrap">
            {topFactors.map((factor, index) => (
              <span key={`${factor}-${index}`} className="overflow-hidden text-ellipsis whitespace-nowrap">
                {index > 0 ? "| " : ""}
                {factor}
              </span>
            ))}
          </div>
        ) : null}
        <div className="flex items-center gap-2 overflow-hidden text-ellipsis text-xs text-slate-500 max-md:flex-nowrap md:flex-wrap">
          <span className="overflow-hidden text-ellipsis whitespace-nowrap">{advancedFactors.join(" | ")}</span>
          {flags.length > 0 ? (
            <span className="flex gap-1 overflow-hidden text-ellipsis">
              {flags.map((flag) => (
                <span key={flag} className="inline-flex overflow-hidden text-ellipsis whitespace-nowrap rounded border border-slate-600/70 bg-slate-800/70 px-1.5 py-0.5 text-[10px] font-semibold text-amber-200">
                  {flag}
                </span>
              ))}
            </span>
          ) : null}
        </div>
        <div className="overflow-hidden text-ellipsis whitespace-nowrap text-xs text-cyan-300/80">
          {compactSummary}
        </div>
      </span>

      <div className="hidden w-[122px] overflow-hidden text-[11px] text-slate-400 md:block">
        <div className="overflow-hidden text-ellipsis whitespace-nowrap">
          {formatRelativeTime(item.timestamp, nowMs)}
        </div>
        <div className="overflow-hidden text-ellipsis whitespace-nowrap text-[10px] text-slate-500">
          {formatTime(item.timestamp)} {item.source ? `| ${item.source}` : ""}
        </div>
      </div>
    </article>
  );
}
