export interface FeedItem {
  id: string;
  symbol: string;
  price: number;
  changePercent: number;
  signalType: "SPIKE" | "BULLISH" | "BEARISH" | "NEWS";
  activityScore: number;
  score?: number;
  confidence?: "HIGH" | "MEDIUM" | "LOW";
  isTopOpportunity?: boolean;
  isTrending?: boolean;
  headline: string;
  timestamp: string;
  source: string;
}

export type FeedFilter = "ALL" | "BULLISH" | "BEARISH" | "NEWS" | "SPIKE";
