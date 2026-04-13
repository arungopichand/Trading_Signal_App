import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { FeedHeader } from "./components/feed/FeedHeader";
import { FeedList } from "./components/feed/FeedList";
import { TopOpportunity } from "./components/feed/TopOpportunity";
import type { FeedFilter, FeedItem } from "./components/feed/types";
import { getPriceRange } from "./components/feed/utils";
import { FEED_HUB_URL, FEED_SIM_STATS_URL, FEED_SIM_URL, FEED_URL } from "./config/api";

const MAX_ITEMS = 100;
const SIGNAL_EXPIRY_SECONDS = 75;
const SCORE_DECAY_START_SECONDS = 30;
const MAX_SCORE_DECAY = 0.55;

interface SimulationStats {
  engineSignalsPerMinute: number;
  fallbackSignalsPerMinute: number;
  totalSignalsPerMinute: number;
  fallbackRatePercent: number;
}

const SIM_SYMBOLS = ["NVDA", "TSLA", "AMD", "PLTR", "AAPL", "META", "SMCI", "IONQ", "RGTI", "AGH"];
const SIM_HEADLINES = [
  "Announces strategic capital update amid strong demand",
  "Receives analyst rating revision after guidance update",
  "Wins new enterprise contract and expands pipeline",
  "Shares active as traders react to overnight commentary",
  "Extends premarket move on heavy order-flow activity",
];
const SIM_SOURCES = ["Reuters", "Bloomberg", "CNBC", "Benzinga", "The Fly"];
const SIM_FLAGS = ["HIGH_CTB", "REG_SHO", "R_S", "HALT", "OFFERING", "FDA"];
const INCOMING_FLUSH_MS = 120;

const requestCache = new Map<string, { expiresAt: number; promise: Promise<unknown> }>();

function randomRange(min: number, max: number): number {
  return min + (Math.random() * (max - min));
}

function pickOne<T>(values: T[]): T {
  return values[Math.floor(Math.random() * values.length)];
}

function normalizeSignalType(value: unknown): FeedItem["signalType"] {
  const input = typeof value === "string" ? value.toUpperCase() : "NEWS";
  if (
    input === "SPIKE" ||
    input === "BULLISH" ||
    input === "BEARISH" ||
    input === "NEWS" ||
    input === "TRENDING" ||
    input === "TOP_OPPORTUNITY"
  ) {
    return input;
  }

  return "NEWS";
}

function normalizeFeedItem(raw: unknown): FeedItem | null {
  if (!raw || typeof raw !== "object") {
    return null;
  }

  const candidate = raw as Record<string, unknown>;
  const symbol = typeof candidate.symbol === "string" ? candidate.symbol.trim().toUpperCase() : "";
  const countryCode = typeof candidate.countryCode === "string" ? candidate.countryCode.trim().toUpperCase() : "US";
  const headline = typeof candidate.headline === "string" ? candidate.headline.trim() : "";
  const id = typeof candidate.id === "string" && candidate.id ? candidate.id : crypto.randomUUID();
  const timestamp = typeof candidate.timestamp === "string" ? candidate.timestamp : new Date().toISOString();
  const source = typeof candidate.source === "string" ? candidate.source : "Scanner";
  const url = typeof candidate.url === "string" ? candidate.url.trim() : "";
  const reason = typeof candidate.reason === "string" ? candidate.reason : "";
  const price = Number(candidate.price);
  const priceRange = typeof candidate.priceRange === "string" ? candidate.priceRange.trim() : "";
  const floatShares = Number(candidate.floatShares);
  const institutionalOwnership = Number(candidate.institutionalOwnership);
  const marketCap = Number(candidate.marketCap);
  const volume = Number(candidate.volume);
  const flagsInput = Array.isArray(candidate.flags)
    ? candidate.flags
    : Array.isArray(candidate.specialFlags)
      ? candidate.specialFlags
      : [];
  const flags = flagsInput
      .filter((flag): flag is string => typeof flag === "string" && flag.trim().length > 0)
      .map((flag) => flag.trim().toUpperCase());
  const changePercent = Number(candidate.changePercent);
  const activityScore = Number(candidate.activityScore);
  const score = Number(candidate.score);
  const volumeRatio = Number(candidate.volumeRatio);
  const momentum = Number(candidate.momentum);
  const acceleration = Number(candidate.acceleration);
  const gapPercent = Number(candidate.gapPercent);
  const repeatCount = Number(candidate.repeatCount);
  const momentumDetectedAt = typeof candidate.momentumDetectedAt === "string"
    ? candidate.momentumDetectedAt
    : undefined;
  const confidence = typeof candidate.confidence === "string"
    ? candidate.confidence.toUpperCase()
    : "LOW";
  const sentimentRaw = typeof candidate.sentiment === "string"
    ? candidate.sentiment
    : typeof candidate.sentimentLabel === "string"
      ? candidate.sentimentLabel
      : "NEUTRAL";
  const sentiment = sentimentRaw.toUpperCase();
  const newsCategory = typeof candidate.newsCategory === "string"
    ? candidate.newsCategory.trim()
    : typeof candidate.category === "string"
      ? candidate.category.trim()
      : "";
  const tradeReadiness = typeof candidate.tradeReadiness === "string"
    ? candidate.tradeReadiness.toUpperCase()
    : "WATCH";
  const isTopOpportunity = candidate.isTopOpportunity === true;
  const isTrending = candidate.isTrending === true;

  if (!symbol || !headline || Number.isNaN(price) || Number.isNaN(changePercent) || Number.isNaN(activityScore)) {
    return null;
  }

  return {
    id,
    symbol,
    countryCode: countryCode || "US",
    price,
    priceRange: priceRange || getPriceRange(price),
    changePercent,
    signalType: normalizeSignalType(candidate.signalType),
    activityScore,
    score: Number.isNaN(score) ? activityScore : score,
    confidence: confidence === "HIGH" || confidence === "MEDIUM" || confidence === "LOW" ? confidence : "LOW",
    tradeReadiness: tradeReadiness === "READY" || tradeReadiness === "WATCH" ? tradeReadiness : "WATCH",
    isTopOpportunity,
    isTrending,
    headline,
    url,
    reason,
    floatShares: Number.isNaN(floatShares) ? undefined : floatShares,
    institutionalOwnership: Number.isNaN(institutionalOwnership) ? undefined : institutionalOwnership,
    marketCap: Number.isNaN(marketCap) ? undefined : marketCap,
    volume: Number.isNaN(volume) ? undefined : volume,
    flags: flags.length > 0 ? [...new Set(flags)] : undefined,
    volumeRatio: Number.isNaN(volumeRatio) ? undefined : volumeRatio,
    momentum: Number.isNaN(momentum) ? undefined : momentum,
    sentiment: sentiment === "BULLISH" || sentiment === "BEARISH" || sentiment === "NEUTRAL" ? sentiment : "NEUTRAL",
    sentimentLabel: sentiment === "BULLISH" || sentiment === "BEARISH" ? sentiment : undefined,
    acceleration: Number.isNaN(acceleration) ? undefined : acceleration,
    gapPercent: Number.isNaN(gapPercent) ? undefined : gapPercent,
    newsCategory: newsCategory || undefined,
    category: newsCategory || undefined,
    repeatCount: Number.isNaN(repeatCount) ? undefined : repeatCount,
    specialFlags: flags.length > 0 ? [...new Set(flags)] : undefined,
    momentumDetectedAt,
    timestamp,
    source,
  };
}

function createLocalSimItem(forceStrong: boolean, existingItems: FeedItem[]): FeedItem {
  const preferred = existingItems.slice(0, 6).map((item) => item.symbol);
  const symbolPool = [...new Set([...preferred, ...SIM_SYMBOLS])];
  const symbol = pickOne(symbolPool);
  const isNewsHeavy = Math.random() < 0.25;
  const direction = Math.random() < 0.55 ? 1 : -1;
  const changePercent = forceStrong
    ? Number((randomRange(3.1, 6.3) * direction).toFixed(2))
    : Number((randomRange(0.4, 2.9) * direction).toFixed(2));
  const score = forceStrong
    ? Number(randomRange(96, 128).toFixed(2))
    : Number(randomRange(56, 92).toFixed(2));
  const signalType: FeedItem["signalType"] = forceStrong
    ? "SPIKE"
    : isNewsHeavy
      ? "NEWS"
      : changePercent >= 0
        ? "BULLISH"
        : "BEARISH";
  const timestamp = new Date().toISOString();
  const momentumDetectedAt = new Date(Date.now() - Math.floor(randomRange(2_000, 16_000))).toISOString();
  const volume = Math.round(randomRange(80_000, 2_800_000));
  const volumeRatio = Number(randomRange(1.1, forceStrong ? 4.2 : 2.6).toFixed(2));
  const sentiment: FeedItem["sentiment"] = changePercent >= 0 ? "BULLISH" : "BEARISH";
  const selectedFlags = Math.random() < 0.3 ? [pickOne(SIM_FLAGS)] : [];
  const selectedCategory = isNewsHeavy ? pickOne(["earnings", "analyst", "contract", "fda", "legal"]) : undefined;
  const price = Number(randomRange(1.2, 280).toFixed(2));

  return {
    id: crypto.randomUUID(),
    symbol,
    countryCode: "US",
    price,
    priceRange: getPriceRange(price),
    changePercent,
    signalType,
    activityScore: score,
    score,
    confidence: score > 100 ? "HIGH" : score > 70 ? "MEDIUM" : "LOW",
    tradeReadiness: score > 100 && volumeRatio > 2 ? "READY" : "WATCH",
    isTopOpportunity: forceStrong && Math.random() < 0.4,
    isTrending: Math.random() < 0.3,
    headline: `${symbol} ${pickOne(SIM_HEADLINES)}`,
    url: `https://example.com/news/${symbol.toLowerCase()}-${Date.now()}`,
    reason: forceStrong
      ? "Momentum breakout + unusual volume + confirming news"
      : "Simulated open-market rotational activity",
    floatShares: Number(randomRange(4, 85).toFixed(2)),
    institutionalOwnership: Number(randomRange(4, 92).toFixed(2)),
    marketCap: Number(randomRange(300, 220_000).toFixed(2)),
    volume,
    flags: selectedFlags,
    volumeRatio,
    momentum: changePercent,
    sentiment,
    sentimentLabel: sentiment,
    acceleration: Number((changePercent * randomRange(0.25, 0.85)).toFixed(2)),
    gapPercent: Number((changePercent * randomRange(0.1, 0.6)).toFixed(2)),
    newsCategory: selectedCategory,
    category: selectedCategory,
    repeatCount: Math.floor(randomRange(1, 5)),
    specialFlags: selectedFlags,
    momentumDetectedAt,
    timestamp,
    source: pickOne(SIM_SOURCES),
  };
}

function mergeIncomingBatch(items: FeedItem[], incomingBatch: FeedItem[]): FeedItem[] {
  if (incomingBatch.length === 0) {
    return items;
  }

  const seen = new Set<string>();
  const incoming: FeedItem[] = [];
  for (const item of incomingBatch) {
    if (seen.has(item.id)) {
      continue;
    }

    seen.add(item.id);
    incoming.push(item);
  }

  const existing = items.filter((item) => !seen.has(item.id));
  return [...incoming, ...existing].slice(0, MAX_ITEMS);
}

async function fetchJsonWithTtl(url: string, ttlMs: number): Promise<unknown> {
  const now = Date.now();
  const cached = requestCache.get(url);
  if (cached && cached.expiresAt > now) {
    return cached.promise;
  }

  const promise = fetch(url, { headers: { Accept: "application/json" } }).then(async (response) => {
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`);
    }

    return response.json();
  });

  requestCache.set(url, { expiresAt: now + ttlMs, promise });
  return promise;
}

function getAgeSeconds(item: FeedItem, nowMs: number): number {
  const timestampMs = Date.parse(item.timestamp);
  if (Number.isNaN(timestampMs)) {
    return 0;
  }

  return Math.max(0, Math.floor((nowMs - timestampMs) / 1000));
}

function getDecayedScore(item: FeedItem, nowMs: number): number {
  const baseScore = item.score ?? item.activityScore;
  const ageSeconds = getAgeSeconds(item, nowMs);
  if (ageSeconds <= SCORE_DECAY_START_SECONDS) {
    return baseScore;
  }

  const decayProgress = Math.min(
    1,
    (ageSeconds - SCORE_DECAY_START_SECONDS) / Math.max(1, SIGNAL_EXPIRY_SECONDS - SCORE_DECAY_START_SECONDS),
  );
  const factor = 1 - (MAX_SCORE_DECAY * decayProgress);
  return Number((baseScore * factor).toFixed(2));
}

function playFallbackAlertTone() {
  const AudioCtx = window.AudioContext || (window as typeof window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!AudioCtx) {
    return;
  }

  const context = new AudioCtx();
  const oscillator = context.createOscillator();
  const gain = context.createGain();
  oscillator.type = "sine";
  oscillator.frequency.value = 880;
  gain.gain.value = 0.0001;
  oscillator.connect(gain);
  gain.connect(context.destination);

  const now = context.currentTime;
  gain.gain.exponentialRampToValueAtTime(0.05, now + 0.02);
  gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.22);

  oscillator.start(now);
  oscillator.stop(now + 0.24);
  oscillator.onended = () => {
    void context.close();
  };
}

function randomPlaybackRate(): number {
  return 0.95 + (Math.random() * 0.1);
}

function App() {
  const [items, setItems] = useState<FeedItem[]>([]);
  const [status, setStatus] = useState<"live" | "degraded" | "offline">("degraded");
  const [filter, setFilter] = useState<FeedFilter>("ALL");
  const [autoScroll, setAutoScroll] = useState(true);
  const [soundEnabled, setSoundEnabled] = useState(true);
  const [simulationMode, setSimulationMode] = useState(false);
  const [focusMode, setFocusMode] = useState(false);
  const [rightPanelTab, setRightPanelTab] = useState<"watchlist" | "simulator">("watchlist");
  const [selectedSignal, setSelectedSignal] = useState<FeedItem | null>(null);
  const connectionRef = useRef<HubConnection | null>(null);
  const itemsRef = useRef<FeedItem[]>([]);
  const feedContainerRef = useRef<HTMLDivElement | null>(null);
  const alertAudioRef = useRef<HTMLAudioElement | null>(null);
  const lastSoundTimeRef = useRef(0);
  const hasUserInteractedRef = useRef(false);
  const soundEnabledRef = useRef(soundEnabled);
  const simLastStrongAtRef = useRef(0);
  const incomingQueueRef = useRef<FeedItem[]>([]);
  const incomingFlushTimerRef = useRef<number | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [simulationStats, setSimulationStats] = useState<SimulationStats | null>(null);

  useEffect(() => {
    soundEnabledRef.current = soundEnabled;
  }, [soundEnabled]);

  useEffect(() => {
    itemsRef.current = items;
  }, [items]);

  useEffect(() => {
    const markInteracted = () => {
      hasUserInteractedRef.current = true;
    };

    window.addEventListener("pointerdown", markInteracted, { passive: true });
    window.addEventListener("keydown", markInteracted, { passive: true });
    return () => {
      window.removeEventListener("pointerdown", markInteracted);
      window.removeEventListener("keydown", markInteracted);
    };
  }, []);

  useEffect(() => {
    const audio = new Audio("/sounds/alert.mp3");
    audio.volume = 0.3;
    audio.preload = "auto";
    alertAudioRef.current = audio;

    return () => {
      alertAudioRef.current = null;
    };
  }, []);

  const playSignalAudio = useCallback((item: FeedItem) => {
    const score = item.score ?? item.activityScore;
    const now = Date.now();
    const canPlaySound = soundEnabledRef.current &&
      hasUserInteractedRef.current &&
      item.signalType === "SPIKE" &&
      score > 90 &&
      now - lastSoundTimeRef.current > 5000;
    if (!canPlaySound) {
      return;
    }

    const audio = alertAudioRef.current;
    if (!audio) {
      return;
    }

    const playSinglePing = (target: HTMLAudioElement) => {
      target.playbackRate = randomPlaybackRate();
      target.currentTime = 0;
      void target.play().catch(() => {
        playFallbackAlertTone();
      });
    };

    const playDoublePing = (target: HTMLAudioElement) => {
      playSinglePing(target);
      window.setTimeout(() => {
        const secondPing = new Audio(target.src);
        secondPing.volume = target.volume;
        playSinglePing(secondPing);
      }, 180);
    };

    if (item.isTopOpportunity) {
      playDoublePing(audio);
    } else {
      playSinglePing(audio);
    }

    lastSoundTimeRef.current = now;
  }, []);

  const flushIncomingSignals = useCallback(() => {
    incomingFlushTimerRef.current = null;
    const batch = incomingQueueRef.current;
    if (batch.length === 0) {
      return;
    }

    incomingQueueRef.current = [];
    setItems((current) => mergeIncomingBatch(current, batch));
    setStatus("live");

    for (const item of batch) {
      playSignalAudio(item);
    }
  }, [playSignalAudio]);

  const enqueueIncomingSignals = useCallback((signals: FeedItem[]) => {
    if (signals.length === 0) {
      return;
    }

    incomingQueueRef.current.push(...signals);
    if (incomingFlushTimerRef.current === null) {
      incomingFlushTimerRef.current = window.setTimeout(flushIncomingSignals, INCOMING_FLUSH_MS);
    }
  }, [flushIncomingSignals]);

  const pushIncomingSignal = useCallback((item: FeedItem) => {
    enqueueIncomingSignals([item]);
  }, [enqueueIncomingSignals]);

  useEffect(() => {
    let isMounted = true;

    const loadInitial = async () => {
      try {
        const payload = await fetchJsonWithTtl(`${FEED_URL}?limit=60`, 2_000);
        const normalized = Array.isArray(payload)
          ? payload.map(normalizeFeedItem).filter(Boolean) as FeedItem[]
          : [];

        if (isMounted) {
          setItems(normalized.slice(0, MAX_ITEMS));
        }
      } catch {
        if (isMounted) {
          setStatus("offline");
        }
      }
    };

    void loadInitial();
    return () => {
      isMounted = false;
      if (incomingFlushTimerRef.current !== null) {
        window.clearTimeout(incomingFlushTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    const interval = window.setInterval(() => {
      setNowMs(Date.now());
    }, 1500);

    return () => {
      window.clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    if (simulationMode) {
      setStatus("live");
      return;
    }

    let disposed = false;
    let retryTimer: number | null = null;

    const connection = new HubConnectionBuilder()
      .withUrl(FEED_HUB_URL, {
        withCredentials: false,
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    connection.on("newSignal", (message: unknown) => {
      const normalized = normalizeFeedItem(message);
      if (!normalized) {
        return;
      }

      pushIncomingSignal(normalized);
    });

    connection.on("newSignals", (messages: unknown) => {
      if (!Array.isArray(messages)) {
        return;
      }

      const normalizedBatch = messages
        .map(normalizeFeedItem)
        .filter(Boolean) as FeedItem[];
      if (normalizedBatch.length === 0) {
        return;
      }

      enqueueIncomingSignals(normalizedBatch);
    });

    connection.onreconnected(() => {
      setStatus("live");
    });

    connection.onreconnecting(() => {
      setStatus("degraded");
    });

    connection.onclose(() => {
      setStatus("offline");
    });

    const startConnection = async () => {
      try {
        if (disposed) {
          return;
        }

        await connection.start();
        setStatus("live");
      } catch (error) {
        console.warn("SignalR connection failed.", error);
        setStatus("offline");
        retryTimer = window.setTimeout(() => {
          void startConnection();
        }, 3000);
      }
    };

    void startConnection();

    return () => {
      disposed = true;
      if (retryTimer) {
        window.clearTimeout(retryTimer);
      }

      void connection.stop();
      connectionRef.current = null;
    };
  }, [simulationMode, enqueueIncomingSignals, pushIncomingSignal]);

  useEffect(() => {
    if (!simulationMode) {
      setSimulationStats(null);
      return;
    }
    let cancelled = false;
    let inFlight = false;

    const pullSimulationBatch = async () => {
      if (cancelled || inFlight) {
        return;
      }

      inFlight = true;
      try {
        const response = await fetch(FEED_SIM_URL, {
          headers: { Accept: "application/json" },
          cache: "no-store",
        });

        if (!response.ok) {
          throw new Error(`Simulation API returned ${response.status}`);
        }

        const payload = (await response.json()) as unknown;
        const normalized = Array.isArray(payload)
          ? payload.map(normalizeFeedItem).filter(Boolean) as FeedItem[]
          : [];
        if (!cancelled) {
          const now = Date.now();
          const forceStrong = now - simLastStrongAtRef.current > randomRange(10_000, 15_000);
          if (forceStrong) {
            simLastStrongAtRef.current = now;
          }

          const incoming = [...normalized];
          const minVisible = 2;
          while (incoming.length < minVisible) {
            incoming.push(createLocalSimItem(forceStrong && incoming.length === 0, itemsRef.current));
          }

          enqueueIncomingSignals(incoming);
        }
      } catch {
        if (!cancelled) {
          setStatus("degraded");
          const now = Date.now();
          const forceStrong = now - simLastStrongAtRef.current > randomRange(10_000, 15_000);
          if (forceStrong) {
            simLastStrongAtRef.current = now;
          }

          const fallbackCount = Math.floor(randomRange(2, 4.99));
          const fallbackItems: FeedItem[] = [];
          for (let index = 0; index < fallbackCount; index += 1) {
            fallbackItems.push(createLocalSimItem(forceStrong && index === 0, itemsRef.current));
          }

          enqueueIncomingSignals(fallbackItems);
        }
      } finally {
        inFlight = false;
      }
    };

    void pullSimulationBatch();
    const interval = window.setInterval(() => {
      void pullSimulationBatch();
    }, 1200);

    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, [simulationMode, enqueueIncomingSignals]);

  useEffect(() => {
    if (!simulationMode) {
      return;
    }

    let cancelled = false;
    const pullStats = async () => {
      try {
        const payload = await fetchJsonWithTtl(FEED_SIM_STATS_URL, 1_500) as Partial<SimulationStats>;
        if (!cancelled) {
          setSimulationStats({
            engineSignalsPerMinute: Number(payload.engineSignalsPerMinute ?? 0),
            fallbackSignalsPerMinute: Number(payload.fallbackSignalsPerMinute ?? 0),
            totalSignalsPerMinute: Number(payload.totalSignalsPerMinute ?? 0),
            fallbackRatePercent: Number(payload.fallbackRatePercent ?? 0),
          });
        }
      } catch {
        if (!cancelled) {
          setSimulationStats(null);
        }
      }
    };

    void pullStats();
    const interval = window.setInterval(() => {
      void pullStats();
    }, 3000);

    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, [simulationMode]);

  useEffect(() => {
    if (!autoScroll) {
      return;
    }

    const container = feedContainerRef.current;
    if (!container) {
      return;
    }

    container.scrollTo({ top: 0, behavior: "smooth" });
  }, [items, autoScroll]);

  const filtered = useMemo(() => {
    const fresh = items.filter((item) => getAgeSeconds(item, nowMs) < SIGNAL_EXPIRY_SECONDS);
    const scoped = filter === "ALL"
      ? fresh
      : fresh.filter((item) => item.signalType === filter);

    return [...scoped].sort((a, b) => {
      if (a.isTopOpportunity && !b.isTopOpportunity) {
        return -1;
      }

      if (!a.isTopOpportunity && b.isTopOpportunity) {
        return 1;
      }

      const aScore = getDecayedScore(a, nowMs);
      const bScore = getDecayedScore(b, nowMs);
      if (bScore !== aScore) {
        return bScore - aScore;
      }

      return Date.parse(b.timestamp) - Date.parse(a.timestamp);
    });
  }, [items, filter, nowMs]);

  const topOpportunity = useMemo(
    () => filtered.find((item) => item.isTopOpportunity) ?? filtered[0],
    [filtered],
  );

  const watchlistSymbols = useMemo(() => {
    const ranked = [...items].sort((a, b) => {
      const bScore = b.score ?? b.activityScore;
      const aScore = a.score ?? a.activityScore;
      return bScore - aScore;
    });

    return ranked
      .map((item) => item.symbol)
      .filter((symbol, index, arr) => arr.indexOf(symbol) === index)
      .slice(0, 8);
  }, [items]);

  const panelItems = useMemo(() => {
    if (!focusMode) {
      return filtered;
    }

    const focusSymbols = new Set<string>(watchlistSymbols.slice(0, 4));
    if (topOpportunity?.symbol) {
      focusSymbols.add(topOpportunity.symbol);
    }

    return filtered.filter((item) => focusSymbols.has(item.symbol));
  }, [filtered, focusMode, topOpportunity, watchlistSymbols]);

  const freshCount = useMemo(
    () => items.filter((item) => getAgeSeconds(item, nowMs) <= 15).length,
    [items, nowMs],
  );
  const staleCount = Math.max(0, items.length - freshCount);

  return (
    <main className="h-screen bg-[#0B0F14] text-white">
      <div className="mx-auto flex h-full max-w-[1800px] flex-col">
        <FeedHeader
          filter={filter}
          autoScroll={autoScroll}
          soundEnabled={soundEnabled}
          simulationMode={simulationMode}
          focusMode={focusMode}
          onFilterChange={setFilter}
          onAutoScrollChange={setAutoScroll}
          onSoundEnabledChange={setSoundEnabled}
          onSimulationModeChange={setSimulationMode}
          onFocusModeChange={setFocusMode}
          status={status}
          simulationStatsLabel={simulationStats
            ? `SIM ENG ${simulationStats.engineSignalsPerMinute}/m | FB ${simulationStats.fallbackSignalsPerMinute}/m | ${simulationStats.fallbackRatePercent.toFixed(0)}%`
            : undefined}
        />
        {simulationMode ? (
          <div className="border-b border-amber-600/40 bg-amber-500/10 px-4 py-2 text-xs font-semibold tracking-wide text-amber-200">
            Simulation mode active (market closed)
          </div>
        ) : null}

        <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 p-3 lg:grid-cols-12">
          <section className="rounded border border-slate-800 bg-slate-950/70 p-3 lg:col-span-12">
            <div className="mb-2 text-xs font-semibold tracking-wide text-slate-300">Market Pulse</div>
            <div className="grid grid-cols-2 gap-2 md:grid-cols-5">
              <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                <div className="text-[11px] text-slate-400">Status</div>
                <div className={`text-sm font-semibold ${status === "live" ? "text-emerald-400" : status === "degraded" ? "text-amber-300" : "text-red-400"}`}>
                  {status.toUpperCase()}
                </div>
              </div>
              <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                <div className="text-[11px] text-slate-400">Signals</div>
                <div className="text-sm font-semibold text-slate-100">{panelItems.length}</div>
              </div>
              <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                <div className="text-[11px] text-slate-400">Fresh</div>
                <div className="text-sm font-semibold text-emerald-400">{freshCount}</div>
              </div>
              <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                <div className="text-[11px] text-slate-400">Stale</div>
                <div className="text-sm font-semibold text-amber-300">{staleCount}</div>
              </div>
              <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                <div className="text-[11px] text-slate-400">Mode</div>
                <div className="text-sm font-semibold text-slate-100">{focusMode ? "FOCUS" : "STANDARD"}</div>
              </div>
            </div>
          </section>

          <section className="min-h-0 rounded border border-slate-800 bg-slate-950/70 lg:col-span-4">
            <div className="border-b border-slate-800 px-3 py-2 text-xs font-semibold tracking-wide text-slate-300">
              Top Opportunity
            </div>
            <TopOpportunity item={topOpportunity} nowMs={nowMs} />
          </section>

          <section className="min-h-0 rounded border border-slate-800 bg-slate-950/70 lg:col-span-5">
            <div className="border-b border-slate-800 px-3 py-2 text-xs font-semibold tracking-wide text-slate-300">
              Live Signals
            </div>
            <div ref={feedContainerRef} className="h-[42vh] overflow-y-auto lg:h-[56vh]">
              <FeedList
                items={panelItems}
                nowMs={nowMs}
                showTopOpportunity={false}
                onRowSelect={(item) => {
                  setSelectedSignal(item);
                  setRightPanelTab("simulator");
                }}
              />
            </div>
          </section>

          <section className="min-h-0 rounded border border-slate-800 bg-slate-950/70 lg:col-span-3">
            <div className="flex items-center border-b border-slate-800 px-3 py-2 text-xs font-semibold tracking-wide text-slate-300">
              <button
                type="button"
                onClick={() => setRightPanelTab("watchlist")}
                className={`mr-2 rounded px-2 py-1 ${rightPanelTab === "watchlist" ? "bg-slate-700 text-white" : "text-slate-400"}`}
              >
                Watchlist
              </button>
              <button
                type="button"
                onClick={() => setRightPanelTab("simulator")}
                className={`rounded px-2 py-1 ${rightPanelTab === "simulator" ? "bg-slate-700 text-white" : "text-slate-400"}`}
              >
                Simulator
              </button>
            </div>
            <div className="h-[42vh] overflow-y-auto p-3 text-sm lg:h-[56vh]">
              {selectedSignal ? (
                <div className="mb-3 rounded border border-slate-800 bg-slate-900/70 p-2">
                  <div className="text-[11px] text-slate-400">Selected Signal</div>
                  <div className="mt-1 font-semibold text-slate-100">
                    {selectedSignal.symbol} {selectedSignal.changePercent >= 0 ? "+" : ""}
                    {selectedSignal.changePercent.toFixed(2)}%
                  </div>
                  <div className="truncate text-[11px] text-slate-300">{selectedSignal.headline}</div>
                </div>
              ) : null}
              {rightPanelTab === "watchlist" ? (
                <ul className="space-y-2">
                  {watchlistSymbols.map((symbol) => (
                    <li key={symbol} className="rounded border border-slate-800 bg-slate-900/60 px-2 py-1.5">
                      {symbol}
                    </li>
                  ))}
                  {watchlistSymbols.length === 0 ? <li className="text-slate-400">No active watchlist symbols.</li> : null}
                </ul>
              ) : (
                <div className="space-y-2 text-slate-200">
                  <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                    <div className="text-[11px] text-slate-400">Engine Signals/min</div>
                    <div className="font-semibold">{simulationStats?.engineSignalsPerMinute ?? 0}</div>
                  </div>
                  <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                    <div className="text-[11px] text-slate-400">Fallback Signals/min</div>
                    <div className="font-semibold">{simulationStats?.fallbackSignalsPerMinute ?? 0}</div>
                  </div>
                  <div className="rounded border border-slate-800 bg-slate-900/60 p-2">
                    <div className="text-[11px] text-slate-400">Fallback Rate</div>
                    <div className="font-semibold">{(simulationStats?.fallbackRatePercent ?? 0).toFixed(1)}%</div>
                  </div>
                </div>
              )}
            </div>
          </section>
        </div>
      </div>
    </main>
  );
}

export default App;
