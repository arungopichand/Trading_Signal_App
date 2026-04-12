import type { FeedItem } from "./types";
import { formatExactTime, formatNumberCompact, getFlagEmoji, getPriceRange } from "./utils";

interface TopOpportunityProps {
  item?: FeedItem;
  nowMs: number;
}

export function TopOpportunity({ item, nowMs }: TopOpportunityProps) {
  if (!item) {
    return (
      <section className="border-b border-yellow-500/20 bg-[#0F141A] px-3 py-3 text-sm text-slate-300">
        No strong opportunity right now
      </section>
    );
  }

  const signal = item.signalType;
  const confidence = item.confidence ?? "LOW";
  const range = item.priceRange || getPriceRange(item.price);
  const flag = getFlagEmoji(item.countryCode);
  const priceText = Number.isFinite(item.price) ? `$${item.price.toLocaleString(undefined, { maximumFractionDigits: 2 })}` : "-";
  const changeText = `${item.changePercent >= 0 ? "+" : ""}${item.changePercent.toFixed(2)}%`;
  const changeClass = item.changePercent >= 0 ? "text-emerald-400" : "text-red-400";
  const signalClass = signal === "SPIKE"
    ? "text-orange-300"
    : signal === "BULLISH"
      ? "text-emerald-300"
      : signal === "BEARISH"
        ? "text-red-300"
        : "text-sky-300";

  const factors = [
    `Vol: ${typeof item.volume === "number" ? formatNumberCompact(item.volume) : "-"}`,
    `RVOL: ${typeof item.volumeRatio === "number" ? `${item.volumeRatio.toFixed(1)}x` : "-"}`,
    `MC: ${typeof item.marketCap === "number" ? formatMarketCap(item.marketCap) : "-"}`,
  ];

  const reasonSet = new Set<string>(item.reasons ?? []);
  if (item.reason) {
    item.reason
      .split("+")
      .map((part) => part.trim())
      .filter(Boolean)
      .forEach((part) => reasonSet.add(part));
  }

  reasonSet.add("Highest ranked");
  const reasonItems = Array.from(reasonSet).slice(0, 4);
  const scannedAt = formatExactTime(item.timestamp);
  const momentumAt = item.momentumDetectedAt ? formatExactTime(item.momentumDetectedAt) : "--:--:-- --";

  return (
    <section className="animate-top-opportunity-in border border-yellow-500/30 bg-[#0F141A] p-3 shadow-[0_0_10px_rgba(255,200,0,0.2)]">
      <div className="text-[11px] font-bold tracking-widest text-amber-200">
        🔥 TOP OPPORTUNITY
      </div>

      <div className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm font-semibold">
        <span>{flag}</span>
        <span className="text-base font-extrabold text-slate-100">{item.symbol}</span>
        {range ? <span className="text-slate-400">{range}</span> : null}
        <span className={`font-bold ${signalClass}`}>{signal}</span>
        <span className="text-xs text-slate-300">({confidence})</span>
        {item.isTrending ? <span className="text-[11px] font-bold text-amber-300">🔥 TRENDING</span> : null}
      </div>

      <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 font-mono text-sm">
        <span className="text-slate-100">{priceText}</span>
        <span className={`font-semibold ${changeClass}`}>{changeText}</span>
      </div>

      <a
        href={item.url || undefined}
        target={item.url ? "_blank" : undefined}
        rel={item.url ? "noopener noreferrer" : undefined}
        className="top-opportunity-headline mt-2 block text-sm text-slate-200 hover:text-white"
        title={item.headline}
      >
        {item.headline}
      </a>

      <div className="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-300">
        {factors.join(" | ")}
      </div>

      <ul className="mt-2 space-y-0.5 text-xs text-slate-300">
        {reasonItems.map((entry) => (
          <li key={entry}>• {entry}</li>
        ))}
      </ul>

      <div className="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] text-slate-400">
        <span>{scannedAt}</span>
        <span>|</span>
        <span>Momentum: {momentumAt}</span>
        <span>|</span>
        <span>{item.source || "Scanner"}</span>
      </div>

      <div className="sr-only">Rendered at {nowMs}</div>
    </section>
  );
}

function formatMarketCap(value: number): string {
  if (!Number.isFinite(value)) {
    return "-";
  }

  if (Math.abs(value) >= 1_000_000_000_000) {
    return `${(value / 1_000_000_000_000).toFixed(1)}T`;
  }

  if (Math.abs(value) >= 1_000_000_000) {
    return `${(value / 1_000_000_000).toFixed(1)}B`;
  }

  if (Math.abs(value) >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }

  return `${Math.round(value)}`;
}
