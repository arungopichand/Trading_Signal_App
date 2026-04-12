import { memo } from "react";
import type { FeedItem as FeedItemType } from "./types";
import { FeedItemHeader } from "./FeedItemHeader";
import { FeedItemHeadline } from "./FeedItemHeadline";
import { FeedItemMeta } from "./FeedItemMeta";

interface FeedItemProps {
  item: FeedItemType;
  nowMs: number;
}

export const FeedItem = memo(function FeedItem({ item, nowMs }: FeedItemProps) {
  const score = item.score ?? item.activityScore;
  const isStrong = score > 95;
  const topClass = item.isTopOpportunity ? "feed-top-opportunity" : "";
  const strengthClass = score > 100 ? "feed-strength-strong" : score > 70 ? "feed-strength-medium" : "feed-strength-weak";
  const timestampMs = Date.parse(item.timestamp);
  const ageSeconds = Number.isNaN(timestampMs) ? 0 : Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
  const timeDecayClass = ageSeconds > 30 ? "feed-age-stale" : ageSeconds > 12 ? "feed-age-aging" : "";
  const flashClass = ageSeconds <= 1 && !item.isTopOpportunity ? "animate-flash" : "";

  return (
    <article className={`border-b border-slate-800/80 px-3 py-2.5 hover:bg-slate-800/30 ${isStrong ? "feed-strong-signal" : ""} ${topClass} ${strengthClass} ${timeDecayClass} ${flashClass}`}>
      <div className="min-w-0 space-y-1 overflow-hidden">
        <FeedItemHeader item={item} />
        <FeedItemHeadline item={item} />
        <FeedItemMeta item={item} nowMs={nowMs} />
      </div>
    </article>
  );
});
