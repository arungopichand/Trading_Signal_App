import type { FeedItem } from "./types";
import { formatExactTime, formatRelativeTime } from "./utils";

interface FeedItemMetaProps {
  item: FeedItem;
  nowMs: number;
}

export function FeedItemMeta({ item, nowMs }: FeedItemMetaProps) {
  const exact = formatExactTime(item.timestamp);
  const relative = formatRelativeTime(item.timestamp, nowMs);
  const move = `${item.changePercent >= 0 ? "+" : ""}${item.changePercent.toFixed(2)}%`;
  const momentum = item.momentumDetectedAt ? formatExactTime(item.momentumDetectedAt) : "--:--:-- --";
  const directionClass = item.changePercent >= 0
    ? "translate-y-[-1px] text-emerald-400"
    : "translate-y-[1px] text-red-400";

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-500">
      <span className="whitespace-nowrap">{exact} ({relative})</span>
      <span className="text-slate-600">|</span>
      <span className={`whitespace-nowrap font-semibold transition-all duration-150 ${directionClass}`}>{move}</span>
      <span className="text-slate-600">|</span>
      <span className="whitespace-nowrap">Momentum: {momentum}</span>
      <span className="text-slate-600">|</span>
      <span className="whitespace-nowrap">{item.source || "Scanner"}</span>
    </div>
  );
}
