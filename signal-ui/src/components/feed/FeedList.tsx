import { FeedRow } from "./FeedRow";
import type { FeedItem } from "./types";

interface FeedListProps {
  items: FeedItem[];
  nowMs: number;
}

export function FeedList({ items, nowMs }: FeedListProps) {
  if (items.length === 0) {
    return (
      <div className="px-4 py-6 text-sm text-slate-400">
        Monitoring market... waiting for high-probability setups
      </div>
    );
  }

  const highPriority = items
    .filter((item) => (item.score ?? item.activityScore) > 90)
    .slice(0, 3);
  const highPriorityIds = new Set(highPriority.map((item) => item.id));
  const normalFeed = items.filter((item) => !highPriorityIds.has(item.id));

  return (
    <section aria-live="polite">
      {highPriority.length > 0 ? (
        <div className="border-b border-slate-800/80 bg-slate-950/80">
          <div className="px-4 py-2 text-[11px] font-bold tracking-widest text-amber-300">
            HIGH PRIORITY
          </div>
          {highPriority.map((item) => (
            <FeedRow key={item.id} item={item} nowMs={nowMs} />
          ))}
        </div>
      ) : null}
      {normalFeed.map((item) => (
        <FeedRow key={item.id} item={item} nowMs={nowMs} />
      ))}
    </section>
  );
}
