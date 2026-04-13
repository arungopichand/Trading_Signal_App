import type { FeedFilter } from "./types";

interface FeedHeaderProps {
  filter: FeedFilter;
  autoScroll: boolean;
  soundEnabled: boolean;
  simulationMode: boolean;
  focusMode: boolean;
  onFilterChange: (filter: FeedFilter) => void;
  onAutoScrollChange: (enabled: boolean) => void;
  onSoundEnabledChange: (enabled: boolean) => void;
  onSimulationModeChange: (enabled: boolean) => void;
  onFocusModeChange: (enabled: boolean) => void;
  status: "live" | "degraded" | "offline";
  simulationStatsLabel?: string;
}

const filters: FeedFilter[] = ["ALL", "SPIKE", "BULLISH", "BEARISH", "TRENDING", "NEWS"];

export function FeedHeader({
  filter,
  autoScroll,
  soundEnabled,
  simulationMode,
  focusMode,
  onFilterChange,
  onAutoScrollChange,
  onSoundEnabledChange,
  onSimulationModeChange,
  onFocusModeChange,
  status,
  simulationStatsLabel,
}: FeedHeaderProps) {
  const statusDot =
    status === "live" ? "bg-emerald-400" : status === "degraded" ? "bg-amber-400" : "bg-red-400";
  const statusLabel = status === "live" ? "LIVE FEED" : status === "degraded" ? "DEGRADED" : "OFFLINE";

  return (
    <header className="sticky top-0 z-20 border-b border-slate-800 bg-slate-950/95 backdrop-blur">
      <div className="flex items-center justify-between px-4 py-2 text-xs text-slate-300">
        <div className="flex items-center gap-2">
          <span className={`h-2 w-2 rounded-full ${statusDot}`} />
          <span className="font-semibold tracking-wide">{statusLabel}</span>
          {simulationMode && simulationStatsLabel ? (
            <span className="rounded border border-amber-600/40 bg-amber-500/10 px-2 py-0.5 text-[11px] font-semibold text-amber-200">
              {simulationStatsLabel}
            </span>
          ) : null}
        </div>
        <div className="flex items-center gap-2">
          {filters.map((value) => (
            <button
              key={value}
              type="button"
              onClick={() => onFilterChange(value)}
              className={`px-2 py-1 text-[11px] font-semibold ${
                filter === value ? "bg-slate-700 text-white" : "text-slate-400 hover:text-slate-200"
              }`}
            >
              {value}
            </button>
          ))}
          <button
            type="button"
            onClick={() => onFocusModeChange(!focusMode)}
            className={`px-2 py-1 text-[11px] font-semibold ${
              focusMode ? "bg-slate-700 text-white" : "text-slate-400 hover:text-slate-200"
            }`}
          >
            FOCUS {focusMode ? "ON" : "OFF"}
          </button>
          <button
            type="button"
            onClick={() => onAutoScrollChange(!autoScroll)}
            className={`px-2 py-1 text-[11px] font-semibold ${
              autoScroll ? "bg-slate-700 text-white" : "text-slate-400 hover:text-slate-200"
            }`}
          >
            AUTO-SCROLL {autoScroll ? "ON" : "OFF"}
          </button>
          <button
            type="button"
            onClick={() => onSoundEnabledChange(!soundEnabled)}
            className={`px-2 py-1 text-[11px] font-semibold ${
              soundEnabled ? "bg-slate-700 text-white" : "text-slate-400 hover:text-slate-200"
            }`}
          >
            SOUND {soundEnabled ? "ON" : "OFF"}
          </button>
          <button
            type="button"
            onClick={() => onSimulationModeChange(!simulationMode)}
            className={`px-2 py-1 text-[11px] font-semibold ${
              simulationMode ? "bg-amber-700 text-amber-50" : "text-slate-400 hover:text-slate-200"
            }`}
          >
            SIM {simulationMode ? "ON" : "OFF"}
          </button>
        </div>
      </div>
      <div className="flex items-center justify-between border-t border-slate-800 px-3 py-1.5 text-[11px] font-bold tracking-widest text-slate-500">
        <span>MARKET PULSE | TOP SIGNAL | LIVE LIST | WATCHLIST</span>
        <span className="hidden md:block">PHASE 2 DASHBOARD</span>
      </div>
    </header>
  );
}
