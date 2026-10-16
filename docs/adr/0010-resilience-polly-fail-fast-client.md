# ADR-0010: Resilience built from Polly v8, a fail-fast Redis client, and "unrequested cancellation = failure"

- **Status:** Accepted
- **Date:** 2026-10-16
- **Milestone:** M5 (implements [ADR-0005](0005-per-route-failure-policy.md); doesn't change it)

## Context
ADR-0005 fixed *what* happens when Redis fails: a 50 ms timeout, a breaker (≥ 50% of ≥ 20 calls in 10 s → open 5 s), payments fail open and refunds fail closed. It didn't fix *how* to build it. Three questions had real alternatives:
1. What implements the timeout and the circuit breaker?
2. What does the Redis client do while it's disconnected?
3. Which exceptions count as "Redis failed"?

Experiment E3 showed why the second question matters. With StackExchange.Redis's defaults, commands are *queued* while disconnected. Each one waits up to the 5 s timeout, and on reconnect the queue is replayed as a burst that drained a well-behaved merchant's bucket (138 false `429`s).

## Decision
1. **Polly v8 (`Polly.Core`) builds the pipeline:** an outer circuit breaker and an inner timeout, in `ResilientRateLimiter`, a decorator over `IRateLimiter` (spec §9.1).
   - Polly's `CircuitBreakerStrategyOptions` map one-to-one onto ADR-0005's numbers (`FailureRatio`, `MinimumThroughput`, `SamplingDuration`, `BreakDuration`).
   - It accepts a `TimeProvider`, so breaker behaviour is tested with `FakeTimeProvider`.
   - Because the Redis client ignores cancellation tokens, the call is wrapped in `Task.WaitAsync(token)` so Polly's timeout can actually abandon it.
2. **The Redis client is configured to fail fast:**
   - `BacklogPolicy.FailFast`: no queueing while disconnected;
   - `AsyncTimeout = SyncTimeout = 50 ms`;
   - `AbortOnConnectFail = false`: the gateway starts without Redis and the client reconnects in the background;
   - `ConnectTimeout = 1 s`.

   Experiment E3 turns all of this off (`RateLimiting:Redis:UseResilience=false`, `TimeoutMs=5000`) to reproduce the defaults.
3. **"Redis failed"** means `RedisException`, `TimeoutException`, Polly's `TimeoutRejectedException`, or an **`OperationCanceledException` the caller didn't request**. A cancellation requested by the caller (the HTTP client hung up) still propagates. Anything else (a bug) is *not* treated as an outage.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Hand-written breaker (a counter, a timestamp, a state enum) | ~80 lines, and fun to write. But the half-open logic (one trial call, then close or re-open), the sliding sampling window and thread-safety are where hand-written breakers usually go wrong. Polly is the .NET standard, so the learning is in configuring it well, and the concept note explains the state machine. |
| `Microsoft.Extensions.Resilience` / `AddResilienceHandler` | Built on Polly v8, but aimed at `HttpClient` pipelines and DI-registered named pipelines. We wrap a Redis call, not HTTP, so plain `Polly.Core` is the smaller dependency. |
| Polly v7 | Superseded. v8 has the `TimeProvider` support we need for deterministic tests. |
| Keep the client's queueing backlog, just with a 50 ms timeout | Each request would still wait the full 50 ms while Redis is known to be down, and the replay burst on reconnect would remain (smaller, but there). Fail-fast makes a known-dead Redis cost microseconds. |
| Treat every `OperationCanceledException` as caller cancellation | What the first version did. P4's `docker pause` run showed the client cancelling its *own* in-flight commands when it abandons a frozen connection, and two of those became `500`s. |
| Treat every exception as an outage | It would hide bugs: a broken Lua script would silently become "degraded mode forever". |

## Consequences
- P4 met spec N4 on both `stop` and `pause` outages, with the caveat recorded in its report: about 60–75 ms over normal at the very tail during the first ~2 s of a *silent* outage, before the breaker opens.
- `ResilientRateLimiter` exposes `CircuitState` and a test-only `TrialCalls` count. Polly only reports "half-open" once a call arrives after the break, so tests can't read it from the state alone.
- A new dependency (`Polly.Core`), pinned in `Directory.Packages.props`.

## Revisit when
- Several dependencies need breakers. Then register named pipelines centrally (`Microsoft.Extensions.Resilience`) instead of building one inline.
- StackExchange.Redis changes how it reports abandoned commands. `ResilientRateLimiterTests` pins the classification, and P4 is the end-to-end check.
