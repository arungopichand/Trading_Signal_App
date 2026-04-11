import type { FeedItem } from "./types";
import { getIndicatorColor, selectRelevantFactors } from "./utils";

interface FeedItemFactorsProps {
  item: FeedItem;
}

export function FeedItemFactors({ item }: FeedItemFactorsProps) {
  const factors = selectRelevantFactors(item);
  if (factors.length === 0) {
    return null;
  }

  const coloredKeys = new Set(
    [...factors]
      .sort((a, b) => b.colorRank - a.colorRank)
      .filter((factor) => factor.colorRank > 0)
      .slice(0, 3)
      .map((factor) => factor.key),
  );

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-400">
      {factors.map((factor, index) => (
        <span
          key={`${factor.key}-${index}`}
          className={`whitespace-nowrap ${coloredKeys.has(factor.key) ? getIndicatorColor(factor.type, factor.value) : "text-slate-400"}`}
        >
          {index > 0 ? "| " : ""}
          {factor.label}
        </span>
      ))}
    </div>
  );
}
