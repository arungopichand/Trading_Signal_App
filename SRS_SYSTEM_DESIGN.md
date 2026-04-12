# Software Requirements Specification (SRS) and System Design
**Project:** Real-Time Stock Intelligence Platform  
**Version:** 1.0  
**Date:** April 11, 2026

## 1. Executive Summary
**Layman view:**  
This system watches stock market activity in near real time, combines data from multiple trusted sources, and highlights the most important opportunities so users can act faster with better context.

**Technical view:**  
The platform is a multi-source signal generation pipeline built on ASP.NET Core and React. It ingests market, news, and fundamentals data from Finnhub, Polygon, NewsAPI, and Financial Modeling Prep (FMP), normalizes into `UnifiedMarketData`, applies confluence scoring and filtering, and publishes ranked signals to clients over SignalR.

---

## 2. System Overview
**Layman view:**  
Think of this as a smart market radar. It continuously scans symbols, looks for unusual activity, checks supporting news, scores the setup, and displays the best signals in a live terminal-style feed.

**Technical view:**  
The system consists of:
1. Backend API + background workers for data ingestion and signal generation.
2. Signal engine with confluence, trend detection, deduplication, and top-opportunity tagging.
3. Real-time push layer via SignalR hub (`/hubs/feed`).
4. React/Vite/Tailwind frontend rendering dense feed rows and a pinned Top Opportunity panel.

---

## 3. Problem Statement
**Layman view:**  
Single-source market tools can fail, become noisy, or miss context. Users need reliable and explainable trade signals, not random alerts.

**Technical view:**  
Core problems addressed:
1. Provider dependency risk and partial outages.
2. High false-positive rate from single-factor triggers.
3. Duplicate/noisy feed updates.
4. Latency and API rate-limit constraints.
5. Need for interpretable signal reasoning for users and stakeholders.

---

## 4. Objectives
1. Integrate multi-provider data with fallbacks.
2. Produce only multi-factor (confluence) signals.
3. Reduce feed noise using deduplication and trending quality gates.
4. Rank and surface one top opportunity per cycle.
5. Keep feed live and non-empty with resilient fallback paths.
6. Support real-time UI updates with low latency.
7. Minimize API load with caching, controlled parallelism, and scan limits.

---

## 5. High-Level Architecture (in words)
**Layman view:**  
Data comes in from market/news/fundamental providers, gets cleaned and combined, then a scoring brain decides what is important. The best items are sent instantly to the dashboard.

**Technical view:**  
1. `SignalBackgroundService` runs periodic scan cycles.
2. `SignalEngine` selects symbols, fetches unified data via `MarketDataService`, computes confluence signals, applies trending boost and deduplication, and marks top opportunity.
3. `FeedService` stores/serves latest items and manages feed-level normalization.
4. Controllers expose REST APIs for feed/symbols/analysis.
5. SignalR `FeedHub` broadcasts updates to frontend subscribers.
6. Frontend consumes initial REST data and streams incremental updates from hub.

---

## 6. Component Breakdown
1. **MarketDataService**  
Layman: Combines all data into one trustworthy stock snapshot.  
Technical: Orchestrates Polygon/Finnhub price+volume, NewsAggregationService news+sentiment, FMP factors; outputs `UnifiedMarketData`; supports cache and top-opportunity cache bypass.

2. **NewsAggregationService**  
Layman: Gets latest relevant news and labels tone (positive/negative/neutral).  
Technical: Primary `NewsAPI` pull, fallback to Finnhub company news, keyword-based sentiment/category scoring, 60s cache.

3. **SignalEngine**  
Layman: Decides whether a stock is worth showing.  
Technical: Confluence factors, score computation, confidence classification, signal type selection, trend boost, dedup, top-opportunity marking, prioritized symbol scanning.

4. **FeedService**  
Layman: Keeps the live feed clean and sorted.  
Technical: Converts signals/news into `FeedItem`, applies feed-level dedup/trending handling, keeps latest feed list with top-opportunity priority.

5. **SimulationSignalService**  
Layman: Creates realistic fake market activity for testing/demo.  
Technical: Generates synthetic inputs with spikes/trends and runs through engine paths to validate UI and pipeline.

6. **External Provider Services**  
Layman: Talk to external data vendors.  
Technical: `PolygonService`, `FinnhubService`, `ExternalNewsApiService`, `FmpService` with provider-specific parsing, retries/fallback behaviors, throttling, cache.

7. **Frontend Feed UI**  
Layman: Shows best opportunities clearly and fast.  
Technical: React components render dense signal rows, pinned `TopOpportunity`, confidence/flags/factors, score-aware ordering, subtle entrance animation.

---

## 7. Data Flow (step-by-step)
1. Background scanner starts cycle.
2. Symbol universe loaded.
3. Engine selects 10-15 symbols using priority + rotation.
4. Controlled parallel fetch executes (`Task.WhenAll` + `SemaphoreSlim`).
5. `MarketDataService` fetches unified market snapshot per symbol.
6. Price source priority: Polygon snapshot first, Finnhub fallback.
7. News source priority: NewsAPI first, Finnhub fallback.
8. Fundamentals loaded from FMP (cached).
9. Unified object built: price, change%, volume, news, sentiment, fundamentals, source metadata.
10. Confluence logic evaluates factors.
11. If factors < 2, signal is skipped.
12. Score/confidence/signal type/reasons generated.
13. Trending engine checks recent occurrences and average score; applies `IsTrending` + boost if rules pass.
14. Dedup engine suppresses near-identical repeats in 5-second window.
15. Top opportunity is selected and tagged.
16. Feed cache/state updated.
17. Results exposed via REST and pushed via SignalR.

---

## 8. Backend Design
**Tech stack:** ASP.NET Core, hosted background services, HttpClient factory, IMemoryCache, SignalR.

**Key design choices:**
1. **Configuration-first keys**  
Reads `FINNHUB__APIKEY`, `POLYGON__APIKEY`, `NEWSAPI__APIKEY`, `FMP__APIKEY`. Missing keys produce warnings, not hard failures.
2. **Resilience-first data strategy**  
Primary + fallback path per data type; minimal fallback object if all providers fail.
3. **Concurrent but bounded execution**  
Parallel symbol scans with bounded concurrency (default clamp 5-10).
4. **Cache layering**  
Provider-level keys (`price:*`, `news:*`, `fundamentals:*`) and unified key (`market:{symbol}`) with dynamic TTL (high volatility ~4s, normal ~9s).
5. **Observability**  
Important source/cache logs: `Polygon used`, `Finnhub fallback used`, `NewsAPI used`, `FMP used`, cache HIT/MISS.

---

## 9. Frontend Design
**Layman view:**  
The screen is designed like a pro trading console: dense, fast, and prioritizing what matters now.

**Technical view:**
1. React + Vite + Tailwind architecture.
2. SignalR connection receives live feed events.
3. `TopOpportunity` is always rendered above feed rows.
4. Feed rows show symbol, price range, signal, confidence, price change, factors, reasons, timestamps, and badges.
5. Styling favors high-density terminal look with selective emphasis colors.
6. New top opportunity uses subtle fade-in; no aggressive flashing.
7. Fallback UI states: no top opportunity and waiting-for-signal states.

---

## 10. API Design (current endpoints)
1. `GET /`  
Returns service metadata and key links.

2. `GET /health`  
Basic health status.

3. `GET /api/feed`  
Latest feed items.

4. `GET /api/feed/simulate`  
Generates and returns simulated feed batch.

5. `GET /api/feed/sim-stats`  
Simulation metrics snapshot.

6. `GET /api/signals/current`  
Current cached stock signals.

7. `GET /api/symbols`  
Current symbol universe.

8. `GET /api/market/unified/{symbol}`  
Returns `UnifiedMarketData` for symbol.

9. `GET /api/market/analyze/{symbol}`  
Returns diagnostic analysis payload:
- `data`
- `factors`
- `scores`
- `signalType`
- `confidence`
- `reason`

10. `GET /api/test/news`  
Finnhub news test endpoint.

11. `GET /api/test/quote/{symbol}`  
Finnhub quote test endpoint.

12. `SignalR Hub: /hubs/feed`  
Real-time feed broadcasting to clients.

---

## 11. Signal Logic Explanation
**Layman view:**  
A stock should not alert just because one thing happened. It alerts when multiple strong signs align together.

**Technical view:**  
Detected factors:
1. `strongMove`: `abs(changePercent) > 2`
2. `volumeSpike`: `volume > configured threshold`
3. `hasNews`: news exists
4. `bullishNews`: sentiment is bullish
5. `bearishNews`: sentiment is bearish

Confluence rule:
1. `factorCount = true conditions`
2. If `factorCount < 2`, discard signal.

Signal type decision:
1. If strong move + volume spike: `SPIKE`
2. Else if bullish news: `BULLISH`
3. Else if bearish news: `BEARISH`
4. Else: `TRENDING` (default/fallback classification path)

Reason array:
1. Strong price move
2. Volume spike
3. Positive news
4. Negative news
5. Trending reason appended when applicable

---

## 12. Scoring System Explanation
**Layman view:**  
Each strong clue adds points. More aligned clues means higher confidence.

**Technical view:**  
Base confluence scoring:
1. `+40` for strong move
2. `+30` for volume spike
3. `+30` for bullish or bearish news

Confidence:
1. `HIGH` when 3+ factors
2. `MEDIUM` when exactly 2 factors
3. `LOW` otherwise

Trending boost:
1. If symbol repeats enough with strong average quality, add `+20` and set `IsTrending = true`.

Analysis endpoint scoring (debug view):
1. Momentum score = `abs(changePercent) * 15` when strong move
2. Volume score = `30` on spike
3. News score = `25` when sentiment news exists
4. Confidence from total score thresholds (`>100 HIGH`, `>70 MEDIUM`, else LOW)

---

## 13. Performance & Scalability
1. **Controlled parallelism**  
`Task.WhenAll` for throughput + `SemaphoreSlim` cap to avoid provider overload.
2. **Scan window limiting**  
Processes only 10-15 symbols per cycle.
3. **Priority + rotation**  
Recently active and trending symbols are scanned first; remaining universe rotates to ensure coverage.
4. **Caching**  
Short TTL for fast-changing market data, longer TTL for slower-changing fundamentals/news.
5. **Deduplication**  
Suppresses repeated near-identical emissions (same symbol, small score change, 5s window).
6. **Fallback feed safety**  
When no strong candidates exist, system emits baseline/fallback signal to keep feed active.
7. **Provider safety features**  
Finnhub throttling and cooldown behavior to handle rate limits gracefully.

---

## 14. Assumptions
1. External APIs may be partially available at any time.
2. Market hours gating can skip closed sessions for scan workflows.
3. Symbol universe quality affects signal quality.
4. Keyword sentiment is acceptable as initial NLP baseline.
5. In-memory cache is sufficient for current single-node deployment.
6. Users need high-density visual output over low-density card UI.

---

## 15. Limitations
1. Keyword sentiment can misclassify nuanced headlines.
2. In-memory state is not shared across multiple backend instances.
3. No guaranteed exactly-once signal delivery in disconnected clients.
4. Provider data latency/coverage differences may create temporary inconsistencies.
5. Current scoring is rule-based, not ML-calibrated.
6. Some fallback signal typing may still default to `TRENDING` in low-context scenarios.

---

## 16. Future Enhancements
1. Distributed cache (Redis) for multi-instance consistency.
2. Event-driven persistence for historical backtesting and analytics.
3. Advanced NLP sentiment/entity extraction (LLM or finance-specific models).
4. Adaptive thresholds by symbol volatility regime.
5. User-level watchlists and personalized ranking.
6. Rate-limit aware scheduler with provider budget forecasting.
7. Explainability UI panel with factor timeline per symbol.
8. A/B tuning framework for score/confluence parameters.
9. Websocket-first market stream aggregation beyond polling snapshots.
10. Alerting integrations (webhook, mobile push, Slack/Teams).

---

## 17. Conclusion
**Layman view:**  
This platform is built to deliver reliable, real-time stock opportunities by confirming signals with multiple sources and multiple factors, not guesswork.

**Technical view:**  
The implemented architecture combines resilient multi-source ingestion, confluence scoring, trend qualification, deduplication, top-opportunity ranking, and real-time delivery. It is production-oriented, observable, and extensible, with clear next steps toward distributed scale and smarter intelligence.
