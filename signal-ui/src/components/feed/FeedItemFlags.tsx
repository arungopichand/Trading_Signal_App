import type { FeedItem } from "./types";
import { getFlagColor, mapFlagLabel } from "./utils";

interface FeedItemFlagsProps {
  item: FeedItem;
}

const visibleFlags = new Set(["HIGH_CTB", "REG_SHO", "R_S", "HALT", "OFFERING", "FDA"]);

export function FeedItemFlags({ item }: FeedItemFlagsProps) {
  const flags = (item.flags ?? [])
    .map((flag) => flag.trim().toUpperCase())
    .filter((flag) => visibleFlags.has(flag));

  if (flags.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1 text-[10px]">
      {flags.map((flag) => (
        <span
          key={flag}
          className={`rounded border px-1.5 py-0.5 font-semibold tracking-wide ${getFlagColor(flag)}`}
        >
          {mapFlagLabel(flag)}
        </span>
      ))}
    </div>
  );
}
