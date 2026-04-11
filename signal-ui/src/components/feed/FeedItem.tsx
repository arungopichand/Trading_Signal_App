import type { FeedItem as FeedItemType } from "./types";
import { FeedItemFactors } from "./FeedItemFactors";
import { FeedItemFlags } from "./FeedItemFlags";
import { FeedItemHeader } from "./FeedItemHeader";
import { FeedItemHeadline } from "./FeedItemHeadline";
import { FeedItemMeta } from "./FeedItemMeta";

interface FeedItemProps {
  item: FeedItemType;
  nowMs: number;
}

export function FeedItem({ item, nowMs }: FeedItemProps) {
  const score = item.score ?? item.activityScore;
  const isStrong = score > 90;
  const isVeryStrong = score > 120;
  const topClass = item.isTopOpportunity ? "feed-top-opportunity" : "";
  const strengthClass = score > 100 ? "feed-strength-strong feed-ultra-strong" : score > 70 ? "feed-strength-medium" : "feed-strength-weak";
  const timestampMs = Date.parse(item.timestamp);
  const ageSeconds = Number.isNaN(timestampMs) ? 0 : Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
  const timeDecayClass = ageSeconds > 30 ? "feed-age-stale" : ageSeconds > 10 ? "feed-age-aging" : "";
  const flashClass = ageSeconds <= 1 && !item.isTopOpportunity ? "animate-flash" : "";
  const strongGlowClass = score > 100
    ? isVeryStrong
      ? "shadow-[0_0_12px_rgba(255,150,0,0.6)]"
      : "shadow-[0_0_8px_rgba(255,200,0,0.4)]"
    : "";
  const topOpportunityEmphasis = item.isTopOpportunity ? "text-[15px]" : "";

  return (
    <article className={`border-b border-slate-900 px-3 py-2 hover:bg-slate-800/45 ${isStrong ? "feed-strong-signal" : ""} ${topClass} ${strengthClass} ${timeDecayClass} ${flashClass} ${strongGlowClass} ${topOpportunityEmphasis}`}>
      <div className="min-w-0 space-y-1 overflow-hidden">
        <FeedItemHeader item={item} />
        <FeedItemHeadline item={item} />
        <FeedItemFactors item={item} />
        <FeedItemFlags item={item} />
        <FeedItemMeta item={item} nowMs={nowMs} />
      </div>
    </article>
  );
}
