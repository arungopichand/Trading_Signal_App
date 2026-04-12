import { memo } from "react";
import { FeedItem } from "./FeedItem";
import { TopOpportunity } from "./TopOpportunity";
import type { FeedItem as FeedItemType } from "./types";

interface FeedListProps {
  items: FeedItemType[];
  nowMs: number;
}

const SIGNAL_EXPIRY_SECONDS = 75;
const SCORE_DECAY_START_SECONDS = 30;
const MAX_SCORE_DECAY = 0.55;

function getAgeSeconds(item: FeedItemType, nowMs: number): number {
  const timestampMs = Date.parse(item.timestamp);
  if (Number.isNaN(timestampMs)) {
    return 0;
  }

  return Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
}

function getDecayedScore(item: FeedItemType, nowMs: number): number {
  const baseScore = item.score ?? item.activityScore;
  const ageSeconds = getAgeSeconds(item, nowMs);
  if (ageSeconds <= SCORE_DECAY_START_SECONDS) {
    return baseScore;
  }

  const decayProgress = Math.min(
    1,
    (ageSeconds - SCORE_DECAY_START_SECONDS) / Math.max(1, SIGNAL_EXPIRY_SECONDS - SCORE_DECAY_START_SECONDS),
  );
  return baseScore * (1 - (MAX_SCORE_DECAY * decayProgress));
}

export const FeedList = memo(function FeedList({ items, nowMs }: FeedListProps) {
  if (items.length === 0) {
    return (
      <div className="px-4 py-6 text-sm text-slate-400">
        Monitoring market... waiting for high-probability setups
      </div>
    );
  }

  const topOpportunity = items.find((item) => item.isTopOpportunity);
  const remaining = topOpportunity ? items.filter((item) => item.id !== topOpportunity.id) : items;
  const highPriority = remaining
    .filter((item) => getDecayedScore(item, nowMs) > 90)
    .slice(0, 3);
  const highPriorityIds = new Set(highPriority.map((item) => item.id));
  if (topOpportunity) {
    highPriorityIds.delete(topOpportunity.id);
  }
  const normalFeed = remaining.filter((item) => !highPriorityIds.has(item.id));

  return (
    <section aria-live="polite">
      <TopOpportunity key={topOpportunity?.id ?? "no-top-opportunity"} item={topOpportunity} nowMs={nowMs} />
      {highPriority.length > 0 ? (
        <div className="border-b border-slate-800/80 bg-slate-950/80">
          <div className="px-3 py-1.5 text-[11px] font-bold tracking-widest text-amber-300">
            HIGH PRIORITY
          </div>
          {highPriority.map((item) => (
            <FeedItem key={item.id} item={item} nowMs={nowMs} />
          ))}
        </div>
      ) : null}
      {normalFeed.map((item) => (
        <FeedItem key={item.id} item={item} nowMs={nowMs} />
      ))}
    </section>
  );
});
