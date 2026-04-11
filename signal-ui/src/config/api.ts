const configuredApi = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.trim();
const configuredHub = (import.meta.env.VITE_SIGNALR_HUB_URL as string | undefined)?.trim();
const browserOrigin = typeof window !== "undefined" ? window.location.origin : "http://localhost:5000";
const browserHost = typeof window !== "undefined" ? window.location.hostname : "localhost";

const defaultBaseUrl = browserOrigin.replace(":5173", ":5000");
const localhostBaseUrl = "http://localhost:5000";

export const API = (configuredApi && configuredApi.length > 0 ? configuredApi : defaultBaseUrl).replace(/\/$/, "");
export const FEED_URL = `${API}/api/feed`;

export const FEED_HUB_URL = (() => {
  if (configuredHub && configuredHub.length > 0) {
    return configuredHub;
  }

  if (browserHost === "localhost") {
    return `${localhostBaseUrl}/hubs/feed`;
  }

  return `${API}/hubs/feed`;
})();
