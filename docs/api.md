# API Reference

## Health and Meta
1. `GET /`
   - Returns API metadata and key routes.
2. `GET /health`
   - Liveness endpoint for platform checks.
3. `GET /health/stream`
   - Stream health endpoint with websocket connection state, reconnect count, subscription count, and stale/rate-limit summary.

## Feed
1. `GET /api/feed`
   - Returns latest feed items.
2. `GET /api/feed/simulate`
   - Returns simulated feed batch.
3. `GET /api/feed/sim-stats`
   - Returns simulation metrics.

## Signals
1. `GET /api/signals/current`
   - Returns current generated stock signals.

## Symbols
1. `GET /api/symbols`
   - Returns scan universe symbols.

## Market
1. `GET /api/market/unified/{symbol}`
   - Returns unified market snapshot for symbol.
2. `GET /api/market/analyze/{symbol}`
   - Returns debug analysis payload with factors, scores, signal type, confidence, reasons.

## Test Endpoints
1. `GET /api/test/news`
2. `GET /api/test/quote/{symbol}`

## Real-time
1. `SignalR hub: /hubs/feed`
   - Pushes real-time feed updates to connected clients.
