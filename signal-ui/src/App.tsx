import { useEffect, useMemo, useRef, useState } from "react";
import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { FeedHeader } from "./components/feed/FeedHeader";
import { FeedList } from "./components/feed/FeedList";
import type { FeedFilter, FeedItem } from "./components/feed/types";
import { FEED_HUB_URL, FEED_URL } from "./config/api";

const MAX_ITEMS = 100;
const SIM_SYMBOLS = ["AAPL", "TSLA", "NVDA", "AMD", "META", "PLTR"] as const;

function pickRandom<T>(items: readonly T[]): T {
  return items[Math.floor(Math.random() * items.length)];
}

function randomInRange(min: number, max: number): number {
  return min + Math.random() * (max - min);
}

function normalizeSignalType(value: unknown): FeedItem["signalType"] {
  const input = typeof value === "string" ? value.toUpperCase() : "NEWS";
  if (input === "SPIKE" || input === "BULLISH" || input === "BEARISH" || input === "NEWS") {
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
  const headline = typeof candidate.headline === "string" ? candidate.headline.trim() : "";
  const id = typeof candidate.id === "string" && candidate.id ? candidate.id : crypto.randomUUID();
  const timestamp = typeof candidate.timestamp === "string" ? candidate.timestamp : new Date().toISOString();
  const source = typeof candidate.source === "string" ? candidate.source : "Scanner";
  const price = Number(candidate.price);
  const changePercent = Number(candidate.changePercent);
  const activityScore = Number(candidate.activityScore);
  const score = Number(candidate.score);
  const confidence = typeof candidate.confidence === "string"
    ? candidate.confidence.toUpperCase()
    : "LOW";
  const isTopOpportunity = candidate.isTopOpportunity === true;
  const isTrending = candidate.isTrending === true;

  if (!symbol || !headline || Number.isNaN(price) || Number.isNaN(changePercent) || Number.isNaN(activityScore)) {
    return null;
  }

  return {
    id,
    symbol,
    price,
    changePercent,
    signalType: normalizeSignalType(candidate.signalType),
    activityScore,
    score: Number.isNaN(score) ? activityScore : score,
    confidence: confidence === "HIGH" || confidence === "MEDIUM" || confidence === "LOW" ? confidence : "LOW",
    isTopOpportunity,
    isTrending,
    headline,
    timestamp,
    source,
  };
}

function mergeNewItem(items: FeedItem[], item: FeedItem): FeedItem[] {
  const next = [item, ...items.filter((existing) => existing.id !== item.id)];
  return next.slice(0, MAX_ITEMS);
}

function generateFakeSignal(): FeedItem {
  const now = new Date().toISOString();
  const roll = Math.random();

  let score = randomInRange(40, 79.9);
  let signalType: FeedItem["signalType"] = pickRandom(["BULLISH", "BEARISH"]);
  let confidence: FeedItem["confidence"] = "LOW";
  let isTopOpportunity = false;

  if (roll < 0.1) {
    score = randomInRange(101, 120);
    signalType = "SPIKE";
    confidence = "HIGH";
    isTopOpportunity = Math.random() < 0.5;
  } else if (roll < 0.4) {
    score = randomInRange(80, 100);
    signalType = pickRandom(["SPIKE", "BULLISH", "BEARISH"]);
    confidence = "MEDIUM";
  }

  const changePercent = randomInRange(-5, 5);
  const resolvedSignal =
    signalType === "SPIKE"
      ? "SPIKE"
      : changePercent >= 0
        ? "BULLISH"
        : "BEARISH";

  return {
    id: crypto.randomUUID(),
    symbol: pickRandom(SIM_SYMBOLS),
    price: Number(randomInRange(100, 900).toFixed(2)),
    changePercent: Number(changePercent.toFixed(2)),
    signalType: resolvedSignal,
    activityScore: Number(score.toFixed(2)),
    score: Number(score.toFixed(2)),
    confidence,
    isTopOpportunity,
    isTrending: Math.random() < 0.2,
    headline: "Simulated market movement",
    timestamp: now,
    source: "SIM",
  };
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
  const connectionRef = useRef<HubConnection | null>(null);
  const feedContainerRef = useRef<HTMLDivElement | null>(null);
  const alertAudioRef = useRef<HTMLAudioElement | null>(null);
  const lastSoundTimeRef = useRef(0);
  const hasUserInteractedRef = useRef(false);
  const soundEnabledRef = useRef(soundEnabled);
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    soundEnabledRef.current = soundEnabled;
  }, [soundEnabled]);

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

  const playSignalAudio = (item: FeedItem) => {
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
  };

  const pushIncomingSignal = (item: FeedItem) => {
    setItems((current) => mergeNewItem(current, item));
    setStatus("live");
    playSignalAudio(item);
  };

  useEffect(() => {
    let isMounted = true;

    const loadInitial = async () => {
      try {
        const response = await fetch(FEED_URL, {
          headers: { Accept: "application/json" },
          cache: "no-store",
        });

        if (!response.ok) {
          throw new Error(`Feed API returned ${response.status}`);
        }

        const payload = (await response.json()) as unknown;
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
    };
  }, []);

  useEffect(() => {
    const interval = window.setInterval(() => {
      setNowMs(Date.now());
    }, 1000);

    return () => {
      window.clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    if (simulationMode) {
      setStatus("live");
      return;
    }

    console.log("Connecting to SignalR...", FEED_HUB_URL);
    let disposed = false;
    let retryTimer: number | null = null;

    const connection = new HubConnectionBuilder()
      .withUrl(FEED_HUB_URL, {
        withCredentials: false,
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    connectionRef.current = connection;

    connection.on("newSignal", (message: unknown) => {
      const normalized = normalizeFeedItem(message);
      if (!normalized) {
        return;
      }

      console.log("LIVE:", normalized);
      pushIncomingSignal(normalized);
    });

    connection.onreconnected(() => {
      console.log("SignalR reconnected.");
      setStatus("live");
    });

    connection.onreconnecting(() => {
      console.log("SignalR reconnecting...");
      setStatus("degraded");
    });

    connection.onclose(() => {
      console.log("SignalR closed.");
      setStatus("offline");
    });

    const startConnection = async () => {
      try {
        if (disposed) {
          return;
        }

        await connection.start();
        console.log("Connected!");
        setStatus("live");
      } catch (error) {
        console.error("SignalR connection failed.", error);
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
  }, [simulationMode]);

  useEffect(() => {
    if (!simulationMode) {
      return;
    }

    const interval = window.setInterval(() => {
      const fake = generateFakeSignal();
      pushIncomingSignal(fake);
    }, 1500);

    return () => {
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
    const scoped = filter === "ALL"
      ? items
      : items.filter((item) => item.signalType === filter);

    return [...scoped].sort((a, b) => {
      if (a.isTopOpportunity && !b.isTopOpportunity) {
        return -1;
      }

      if (!a.isTopOpportunity && b.isTopOpportunity) {
        return 1;
      }

      const aScore = a.score ?? a.activityScore;
      const bScore = b.score ?? b.activityScore;
      if (bScore !== aScore) {
        return bScore - aScore;
      }

      return Date.parse(b.timestamp) - Date.parse(a.timestamp);
    });
  }, [items, filter]);

  return (
    <main className="h-screen bg-[#0B0F14] text-white">
      <div className="mx-auto flex h-full max-w-[1800px] flex-col">
        <FeedHeader
          filter={filter}
          autoScroll={autoScroll}
          soundEnabled={soundEnabled}
          simulationMode={simulationMode}
          onFilterChange={setFilter}
          onAutoScrollChange={setAutoScroll}
          onSoundEnabledChange={setSoundEnabled}
          onSimulationModeChange={setSimulationMode}
          status={status}
        />
        {simulationMode ? (
          <div className="border-b border-amber-600/40 bg-amber-500/10 px-4 py-2 text-xs font-semibold tracking-wide text-amber-200">
            Simulation mode active (market closed)
          </div>
        ) : null}
        <div ref={feedContainerRef} className="min-h-0 flex-1 overflow-y-auto">
          <FeedList items={filtered} nowMs={nowMs} />
        </div>
      </div>
    </main>
  );
}

export default App;
