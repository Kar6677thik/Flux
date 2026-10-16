# Timeouts and circuit breakers

> Field Guide: §17.1 Timeout, §17.5 Circuit Breaker, §17.7 Fallback, §17.8 Graceful Degradation, §17.21 Liveness vs. Readiness · Introduced in: M5
> Code: [`RateLimiting/ResilientRateLimiter.cs`](../../src/Flux.Gateway/RateLimiting/ResilientRateLimiter.cs), [`RateLimiting/RedisConnection.cs`](../../src/Flux.Gateway/RateLimiting/RedisConnection.cs) · Decisions: [ADR-0005](../adr/0005-per-route-failure-policy.md), [ADR-0010](../adr/0010-resilience-polly-fail-fast-client.md) · Experiments: [E3](../reports/E3-redis-outage-default-timeouts.md) → [P4](../reports/P4-redis-outage-resilient.md)

## Problem
A dependency that's **down** is bad; one that's **slow** is worse. E3 measured it: with the Redis client's defaults, an outage made every request wait about 5.5 s and then fail. Threads and connections pile up behind the waits, and a rate-limiter problem becomes a total outage of everything, including payments.

## Context
Every call over a network needs an answer to "how long will I wait?" and "should I even try?". These two patterns, from Michael Nygard's *Release It!*, are the standard answers, built into every resilience library (Polly, resilience4j, Hystrix, Envoy).

## Solution

### 1. Timeout: bound the wait
Decide in advance how long an answer is worth waiting for. A rate-limit check that normally takes about 1 ms isn't worth 5 s. Flux allows **50 ms** (ADR-0005). In P4, with Redis frozen (`docker pause`), the slowest requests took 74–85 ms in total, not 6 s.

A timeout only works if something *enforces* it:
- The Redis client's own `AsyncTimeout` is set to 50 ms.
- The client ignores cancellation tokens, so the call is wrapped in `Task.WaitAsync(token)`. That lets Polly's timeout *abandon* the wait, even though it can't stop the command.

### 2. Fail fast instead of queueing
While disconnected, StackExchange.Redis by default *queues* commands. In E3 each queued command waited out its timeout, and on reconnect the queue was replayed as one burst (138 false `429`s for a merchant within its plan). `BacklogPolicy.FailFast` rejects immediately while disconnected. A *known*-dead Redis then costs microseconds.

### 3. Circuit breaker: stop trying for a while
After repeated failures, even 50 ms per request is waste, and the dependency may need the load removed in order to recover. A breaker is a small state machine:
```
           ≥ 50% of ≥ 20 calls failed within 10 s
 CLOSED ─────────────────────────────────────────▶ OPEN ── every call fails at once,
 (calls go to Redis)                                │      no Redis traffic at all
     ▲                                              │ after 5 s
     │ trial succeeds                               ▼
     └──────────────────────────────────────── HALF-OPEN ── exactly one trial call
                         trial fails → back to OPEN for another 5 s
```
In P4 the breaker opened about 1–2 s into a silent outage. From then on even paused-Redis latency was back to normal: p99 about 13 ms against 60–68 ms in the first two seconds.

### 4. Then a deliberate answer
What happens *instead* is the [failure policy](fail-open-vs-fail-closed.md): payments use a local fallback bucket, refunds get a `503`.

### Readiness stays out of it
`/readyz` never checks Redis (spec §11.2). If it did, a Redis outage would mark every gateway unready, the load balancer would remove them all, and "degraded" would become "down".

## Alternatives
- **Retries:** the opposite instinct. They're fine for idempotent, rare failures with backoff and jitter, but during an outage retries *multiply* load on the struggling dependency. There are none here: a rate-limit check that fails gets a policy decision, not a second attempt.
- **Bulkhead** (§17.6): cap concurrent calls to Redis so a slow Redis can't exhaust threads. The timeout plus fail-fast make it unnecessary at our scale.
- **Adaptive timeouts** (set from the observed p99): nicer, but they need history. A fixed 50 ms against a ~1 ms normal leaves ample headroom.

## Trade-offs
- **Timeouts cause false failures** when the dependency is merely slow, for example a GC pause on Redis. 50 ms against a ~1 ms normal leaves room for ~50× slowness before tripping.
- **A breaker delays recovery** by up to its break duration, plus the client's reconnect. P4 recovered in 4.1–4.6 s.
- **Tuning is guesswork until measured**, which is why P4 exists and why the numbers sit in one place in code (`ResilientRateLimiter` constants).

## Implementation
- **Polly v8** pipeline: `AddCircuitBreaker(...)` (outer, so timeouts count as failures) → `AddTimeout(50 ms)` (inner). It runs on the injected `TimeProvider`.
- **What counts as a failure** (`IsRedisFailure`): Redis errors, timeouts, and a cancellation *nobody asked for*. P4's first `pause` run produced two `500`s: the client cancelled its own in-flight commands when it gave up on the frozen connection, and the first version let all cancellations through as "the client went away". A cancellation from the caller's own token still propagates.
- **Visibility:** `flux_ratelimiter_circuit_state` (0 closed, 1 open, 2 half-open), and Warning-level logs on every transition. Per-request fallback logs are Debug, because during an outage they would otherwise flood.

## Testing
- **Breaker state machine** with `FakeTimeProvider`, against a dead Redis port ([`RedisResilienceTests`](../../tests/Flux.Gateway.IntegrationTests/RedisResilienceTests.cs)): 19 failures → still closed; the 20th → open; still serving (degraded) while open; `Advance(5 s)` → one trial call → open again.
- **A real outage mid-test:** a dedicated Redis container is stopped between two requests.
- **Exception classification** ([`ResilientRateLimiterTests`](../../tests/Flux.Gateway.UnitTests/ResilientRateLimiterTests.cs)), including the cancellation case found by P4.
- **End to end with real time:** E3 and P4.

## Observability
In production, alert on `flux_ratelimiter_circuit_state == 1` for longer than about 30 s, and on `rate(flux_ratelimiter_fallback_total[1m]) > 0`.

## Related
[fail-open-vs-fail-closed](fail-open-vs-fail-closed.md) · [redis-basics](redis-basics.md) · [coordinated-omission](coordinated-omission.md) (E3's dropped iterations) · ADR-0005, ADR-0010
