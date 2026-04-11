import type { FeedItem } from "./types";
import { FeedItem as FeedItemView } from "./FeedItem";

interface TopOpportunityProps {
  item: FeedItem;
  nowMs: number;
}

export function TopOpportunity({ item, nowMs }: TopOpportunityProps) {
  return (
    <section className="border-b border-amber-500/40 bg-amber-500/10 shadow-[0_0_10px_rgba(251,191,36,0.22)]">
      <div className="px-3 py-1.5 text-[11px] font-bold tracking-widest text-amber-200">
        TOP OPPORTUNITY
      </div>
      <div className="px-3 pb-2 text-xs text-amber-100/85">
        {item.reason || "Strong multi-factor setup"}
      </div>
      <FeedItemView item={item} nowMs={nowMs} />
    </section>
  );
}
