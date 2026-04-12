# Fault Injection Matrix

## Scenarios

1. `Finnhub WebSocket disconnect`
- Trigger: stop network to `wss://ws.finnhub.io` / kill socket.
- Expect: reconnect counter increases, exponential backoff with jitter, symbols resubscribed, no process crash.

2. `Finnhub REST HTTP 429`
- Trigger: mock handler returns `429`.
- Expect: provider failure increments, rate-limit hits increment, no retry storm, fallback to Polygon/cache.

3. `Polygon HTTP 403/429`
- Trigger: mock handler returns `403` then `429`.
- Expect: Polygon path suppressed by breaker/cooldown, no repeated bursts.

4. `Network timeout`
- Trigger: handler throws `TaskCanceledException`.
- Expect: no crash, breaker counters update, fallback chain remains stable.

5. `Malformed JSON`
- Trigger: handler returns invalid JSON body.
- Expect: parse exception handled, provider failure increments, fallback continues.

## Assertions

- stream-first order always preserved: `WebSocket -> Finnhub -> Polygon -> Cache`
- no parallel multi-provider calls in single symbol path
- fallback payload is non-null and marks `isFallback=true`, `isStale=true`
- active symbol count remains bounded over soak
- no unbounded reconnect loops (backoff increases up to max)
