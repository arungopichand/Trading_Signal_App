export const API = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.trim() ?? "";

export const SIGNALS_URL = `${API.replace(/\/$/, "")}/api/signals/current`;
