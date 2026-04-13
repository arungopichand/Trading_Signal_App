const configuredApi = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.trim();
const browserHost = typeof window !== "undefined" ? window.location.hostname : "localhost";
const isLocalHost = browserHost === "localhost" || browserHost === "127.0.0.1";
const localhostBaseUrl = "http://localhost:5013";

if (!configuredApi && !isLocalHost) {
  throw new Error("Missing VITE_API_BASE_URL. Configure it in the Vercel environment variables.");
}

export const API = (configuredApi && configuredApi.length > 0 ? configuredApi : localhostBaseUrl).replace(/\/$/, "");
export const FEED_URL = `${API}/api/feed`;
export const FEED_SIM_URL = `${API}/api/feed/simulate`;
export const FEED_SIM_STATS_URL = `${API}/api/feed/sim-stats`;
export const FEED_HUB_URL = `${API}/hubs/feed`;
export const METRICS_SUMMARY_URL = `${API}/metrics/summary`;
export const HEALTH_STREAM_URL = `${API}/health/stream`;
export const HEALTH_FINNHUB_URL = `${API}/health/finnhub`;
