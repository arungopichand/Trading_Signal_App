# Architecture

## Overview
The system is a real-time stock intelligence platform:
- Multi-source market ingestion (Polygon, Finnhub, NewsAPI, FMP)
- Unified market model generation
- Confluence signal scoring and filtering
- Real-time broadcast to UI via SignalR

## Logical Components
1. **Ingress Layer**
   - `PolygonService`
   - `FinnhubService`
   - `ExternalNewsApiService`
   - `FmpService`
2. **Aggregation Layer**
   - `NewsAggregationService`
   - `MarketDataService`
3. **Decision Layer**
   - `SignalEngine`
   - Trending + dedup + top-opportunity logic
4. **Delivery Layer**
   - REST controllers
   - `FeedHub` (SignalR)
5. **Presentation Layer**
   - React feed UI + TopOpportunity panel

## Data Priority
1. Price/Change: Polygon primary, Finnhub fallback
2. Volume: Polygon primary, Finnhub fallback
3. News: NewsAPI primary, Finnhub fallback
4. Fundamentals: FMP with cache

## Availability Model
- Missing provider keys do not crash startup.
- All-provider failure returns minimal fallback market object.
- Feed never intentionally returns empty in active cycles.

## Scalability Foundation
- Bounded parallel scans (`SemaphoreSlim`)
- Scan window throttling (10-15 symbols/cycle)
- Priority scanning (active/trending first)
- In-memory cache with volatility-aware TTL

## Runtime Environments
- `dev`: staging environment
- `main`: production environment
