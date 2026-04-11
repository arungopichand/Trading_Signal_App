import type { FeedItem } from "./types";

type IndicatorType = "volume" | "momentum" | "sentiment" | "float" | "io" | "marketCap" | "volumeRatio" | "gap" | "acceleration" | "default";

export interface RelevantFactor {
  key: string;
  label: string;
  type: IndicatorType;
  value?: number | string;
  colorRank: number;
}

export function getFlagEmoji(countryCode?: string): string {
  const code = countryCode?.trim().toUpperCase();
  if (!code || code.length !== 2) {
    return "\u{1F1FA}\u{1F1F8}";
  }

  return code.replace(/./g, (char) => String.fromCodePoint(127397 + char.charCodeAt(0)));
}

export function getPriceRange(price: number): string {
  if (!Number.isFinite(price) || price <= 0) {
    return "";
  }

  if (price < 2) {
    return "< $2";
  }

  if (price < 4) {
    return "< $4";
  }

  if (price < 10) {
    return "< $10";
  }

  return "> $10";
}

export function formatExactTime(timestamp?: string): string {
  if (!timestamp) {
    return "--:--:-- --";
  }

  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) {
    return "--:--:-- --";
  }

  return date.toLocaleTimeString("en-US", {
    hour12: true,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

export function formatRelativeTime(timestamp: string, nowMs: number): string {
  const timeMs = Date.parse(timestamp);
  if (Number.isNaN(timeMs)) {
    return "--";
  }

  const seconds = Math.max(0, Math.floor((nowMs - timeMs) / 1000));
  if (seconds < 60) {
    return `${seconds}s ago`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours}h ago`;
}

export function formatNumberCompact(value?: number): string {
  if (typeof value !== "number" || Number.isNaN(value)) {
    return "-";
  }

  if (Math.abs(value) >= 1_000_000_000) {
    return `${(value / 1_000_000_000).toFixed(1)}B`;
  }

  if (Math.abs(value) >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }

  if (Math.abs(value) >= 1_000) {
    return `${(value / 1_000).toFixed(0)}k`;
  }

  return `${Math.round(value)}`;
}

function formatSignedPercent(value?: number): string {
  if (typeof value !== "number" || Number.isNaN(value)) {
    return "-";
  }

  return `${value >= 0 ? "+" : ""}${value.toFixed(1)}%`;
}

export function mapFlagLabel(flag: string): string {
  return flag.toUpperCase() === "HIGH_CTB"
    ? "High CTB"
    : flag.toUpperCase() === "REG_SHO"
      ? "Reg SHO"
      : flag.toUpperCase() === "R_S"
        ? "R/S"
        : flag.replace(/_/g, " ");
}

export function getIndicatorColor(type: IndicatorType, value?: number | string): string {
  if (type === "volume") {
    const numeric = typeof value === "number" ? value : Number(value);
    if (!Number.isNaN(numeric)) {
      if (numeric >= 1_000_000) {
        return "text-orange-400 font-semibold";
      }
      if (numeric >= 250_000) {
        return "text-yellow-400";
      }
    }
    return "text-slate-400";
  }

  if (type === "momentum") {
    const numeric = typeof value === "number" ? value : Number(value);
    if (Number.isNaN(numeric)) {
      return "text-slate-400";
    }

    return numeric >= 0 ? "text-emerald-400" : "text-red-400";
  }

  if (type === "sentiment") {
    if (value === "BULLISH") {
      return "text-emerald-400";
    }

    if (value === "BEARISH") {
      return "text-red-400";
    }

    return "text-slate-400";
  }

  if (type === "float") {
    const numeric = typeof value === "number" ? value : Number(value);
    if (!Number.isNaN(numeric) && numeric < 5) {
      return "text-emerald-400";
    }

    return "text-slate-400";
  }

  if (type === "io") {
    const numeric = typeof value === "number" ? value : Number(value);
    if (!Number.isNaN(numeric)) {
      if (numeric > 50) {
        return "text-emerald-400";
      }
      if (numeric > 20) {
        return "text-yellow-400";
      }
    }

    return "text-slate-400";
  }

  return "text-slate-400";
}

export function getFlagColor(flag: string): string {
  switch (flag.trim().toUpperCase()) {
    case "HIGH_CTB":
      return "bg-violet-700/80 text-white border-violet-500/60";
    case "REG_SHO":
      return "bg-red-700/80 text-white border-red-500/60";
    case "R_S":
      return "bg-amber-500/90 text-slate-900 border-amber-300/80";
    case "HALT":
      return "bg-slate-700 text-white border-slate-500/70";
    case "OFFERING":
      return "bg-fuchsia-700/80 text-white border-fuchsia-500/60";
    case "FDA":
      return "bg-sky-700/80 text-white border-sky-500/60";
    default:
      return "bg-slate-700 text-white border-slate-500/70";
  }
}

export function selectRelevantFactors(item: FeedItem): RelevantFactor[] {
  const factors: RelevantFactor[] = [];
  const sentimentLabel = item.sentiment && item.sentiment !== "NEUTRAL"
    ? item.sentiment === "BULLISH" ? "Bullish" : "Bearish"
    : "";

  if (typeof item.volume === "number") {
    factors.push({
      key: "volume",
      label: `Vol: ${formatNumberCompact(item.volume)}`,
      type: "volume",
      value: item.volume,
      colorRank: 3,
    });
  }

  if (typeof item.volumeRatio === "number") {
    factors.push({
      key: "volumeRatio",
      label: `RVOL: ${item.volumeRatio.toFixed(1)}x`,
      type: "volume",
      value: item.volumeRatio * 250_000,
      colorRank: 2,
    });
  }

  if (typeof item.momentum === "number") {
    factors.push({
      key: "momentum",
      label: `Momentum: ${formatSignedPercent(item.momentum)}`,
      type: "momentum",
      value: item.momentum,
      colorRank: 4,
    });
  }

  if (typeof item.floatShares === "number") {
    factors.push({
      key: "float",
      label: `Float: ${formatNumberCompact(item.floatShares)}M`,
      type: "float",
      value: item.floatShares,
      colorRank: 2,
    });
  }

  if (typeof item.marketCap === "number") {
    factors.push({
      key: "marketCap",
      label: `MC: ${formatNumberCompact(item.marketCap)}M`,
      type: "marketCap",
      value: item.marketCap,
      colorRank: 0,
    });
  }

  if (typeof item.institutionalOwnership === "number") {
    factors.push({
      key: "io",
      label: `IO: ${item.institutionalOwnership.toFixed(2)}%`,
      type: "io",
      value: item.institutionalOwnership,
      colorRank: 2,
    });
  }

  if (sentimentLabel) {
    factors.push({
      key: "sentiment",
      label: `Sentiment: ${sentimentLabel}`,
      type: "sentiment",
      value: item.sentiment,
      colorRank: 3,
    });
  }

  if (item.newsCategory) {
    factors.push({
      key: "category",
      label: `Category: ${item.newsCategory}`,
      type: "default",
      value: item.newsCategory,
      colorRank: 0,
    });
  }

  if (typeof item.gapPercent === "number") {
    factors.push({
      key: "gap",
      label: `Gap: ${formatSignedPercent(item.gapPercent)}`,
      type: "momentum",
      value: item.gapPercent,
      colorRank: 1,
    });
  }

  if (typeof item.acceleration === "number") {
    factors.push({
      key: "acceleration",
      label: `Accel: ${formatSignedPercent(item.acceleration)}`,
      type: "momentum",
      value: item.acceleration,
      colorRank: 1,
    });
  }

  if (typeof item.repeatCount === "number" && item.repeatCount > 0) {
    factors.push({
      key: "repeats",
      label: `Repeats: ${item.repeatCount}`,
      type: "default",
      value: item.repeatCount,
      colorRank: 1,
    });
  }

  if (item.signalType === "SPIKE") {
    return factors
      .filter((factor) => ["volume", "volumeRatio", "momentum", "float"].includes(factor.key))
      .slice(0, 4);
  }

  if (item.signalType === "NEWS") {
    return factors
      .filter((factor) => ["sentiment", "category", "marketCap", "io"].includes(factor.key))
      .slice(0, 4);
  }

  if (item.signalType === "TRENDING") {
    return factors
      .filter((factor) => ["momentum", "repeats", "volume", "volumeRatio"].includes(factor.key))
      .slice(0, 4);
  }

  return factors
    .filter((factor) => ["momentum", "gap", "volumeRatio", "acceleration"].includes(factor.key))
    .slice(0, 4);
}
