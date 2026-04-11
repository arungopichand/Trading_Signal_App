import type { FeedFilter } from "./types";

interface FeedHeaderProps {
  filter: FeedFilter;
  autoScroll: boolean;
  soundEnabled: boolean;
  simulationMode: boolean;
  onFilterChange: (filter: FeedFilter) => void;
  onAutoScrollChange: (enabled: boolean) => void;
  onSoundEnabledChange: (enabled: boolean) => void;
  onSimulationModeChange: (enabled: boolean) => void;
  status: "live" | "degraded" | "offline";
}

const filters: FeedFilter[] = ["ALL", "SPIKE", "BULLISH", "BEARISH", "NEWS"];

export function FeedHeader({
  filter,
  autoScroll,
  soundEnabled,
  simulationMode,
  onFilterChange,
  onAutoScrollChange,
  onSoundEnabledChange,
  onSimulationModeChange,
  status,
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
            {soundEnabled ? "\u{1F50A} SOUND ON" : "\u{1F507} SOUND OFF"}
          </button>
          <button
            type="button"
            onClick={() => onSimulationModeChange(!simulationMode)}
            className={`px-2 py-1 text-[11px] font-semibold ${
              simulationMode ? "bg-amber-700 text-amber-50" : "text-slate-400 hover:text-slate-200"
            }`}
          >
            {simulationMode ? "\u{1F9EA} SIMULATION MODE ON" : "\u{1F9EA} SIMULATION MODE OFF"}
          </button>
        </div>
      </div>
      <div className="grid grid-cols-[130px_100px_92px_180px_minmax(0,1fr)_92px_92px] gap-2 border-t border-slate-800 px-4 py-2 text-[11px] font-bold tracking-widest text-slate-400">
        <span>SYMBOL</span>
        <span>PRICE</span>
        <span>CHANGE%</span>
        <span>SIGNAL</span>
        <span>NEWS</span>
        <span>TIME</span>
        <span>SOURCE</span>
      </div>
    </header>
  );
}
