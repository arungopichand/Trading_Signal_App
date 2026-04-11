export interface FeedItem {
  id: string;
  symbol: string;
  countryCode?: string;
  price: number;
  priceRange?: string;
  changePercent: number;
  signalType: "SPIKE" | "BULLISH" | "BEARISH" | "NEWS" | "TRENDING" | "TOP_OPPORTUNITY";
  activityScore: number;
  score?: number;
  confidence?: "HIGH" | "MEDIUM" | "LOW";
  tradeReadiness?: "READY" | "WATCH";
  isTopOpportunity?: boolean;
  isTrending?: boolean;
  headline: string;
  url?: string;
  reason?: string;
  reasons?: string[];
  floatShares?: number;
  institutionalOwnership?: number;
  marketCap?: number;
  volume?: number;
  flags?: string[];
  volumeRatio?: number;
  momentum?: number;
  sentiment?: "BULLISH" | "BEARISH" | "NEUTRAL";
  sentimentLabel?: string;
  acceleration?: number;
  gapPercent?: number;
  newsCategory?: string;
  category?: string;
  repeatCount?: number;
  specialFlags?: string[];
  momentumDetectedAt?: string;
  timestamp: string;
  source: string;
}

export type FeedFilter = "ALL" | "BULLISH" | "BEARISH" | "NEWS" | "SPIKE" | "TRENDING";
