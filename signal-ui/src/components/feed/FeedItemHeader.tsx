import type { FeedItem } from "./types";
import { getFlagEmoji, getPriceRange } from "./utils";

interface FeedItemHeaderProps {
  item: FeedItem;
}

const signalLabel: Record<FeedItem["signalType"], string> = {
  SPIKE: "\u{1F525} SPIKE",
  BULLISH: "\u{1F4C8} BULLISH",
  BEARISH: "\u{1F4C9} BEARISH",
  NEWS: "\u{1F4F0} NEWS",
  TRENDING: "\u{1F525} TRENDING",
  TOP_OPPORTUNITY: "\u{1F525} TOP_OPPORTUNITY",
};

const signalStyle: Record<FeedItem["signalType"], string> = {
  SPIKE: "text-amber-200",
  BULLISH: "text-emerald-300",
  BEARISH: "text-red-300",
  NEWS: "text-sky-300",
  TRENDING: "text-orange-200",
  TOP_OPPORTUNITY: "text-amber-100",
};

export function FeedItemHeader({ item }: FeedItemHeaderProps) {
  const confidence = item.confidence ?? "LOW";
  const range = item.priceRange || getPriceRange(item.price);
  const pulseClass = item.signalType === "SPIKE" ? "animate-pulse-soft" : "";
  const topClass = item.isTopOpportunity ? "text-amber-100/95" : "";

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 overflow-hidden text-sm font-semibold">
      <span className={`shrink-0 ${pulseClass}`}>{getFlagEmoji(item.countryCode)}</span>
      <span className={`shrink-0 text-slate-100 ${pulseClass} ${topClass}`}>{item.symbol}</span>
      {range ? <span className="shrink-0 text-slate-400">{range}</span> : null}
      <span className={`shrink-0 ${signalStyle[item.signalType]} ${pulseClass}`}>{signalLabel[item.signalType]}</span>
      <span className="shrink-0 text-xs text-slate-300">({confidence})</span>
    </div>
  );
}
