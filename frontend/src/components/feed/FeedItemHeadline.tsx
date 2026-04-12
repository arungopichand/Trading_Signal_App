import type { FeedItem } from "./types";

interface FeedItemHeadlineProps {
  item: FeedItem;
}

export function FeedItemHeadline({ item }: FeedItemHeadlineProps) {
  if (item.url) {
    return (
      <a
        href={item.url}
        target="_blank"
        rel="noopener noreferrer"
        className="block truncate text-sm text-slate-100 underline-offset-2 hover:text-white hover:underline"
        title={item.headline}
      >
        {item.headline}
      </a>
    );
  }

  return <span className="block truncate text-sm text-slate-100" title={item.headline}>{item.headline}</span>;
}
