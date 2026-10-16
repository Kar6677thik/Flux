# Journal 05 — Redis + resilience (M5)

- **Date:** 2026-10-16
- **Milestone:** M5
- **Branch:** `m5-redis-resilience` (built on `m4-break-it`; the PR targets `main`)

## What we did
1. **[`token_bucket.lua`](../../src/Flux.Gateway/RateLimiting/token_bucket.lua)**: read → refill → decide → write → expire in **one atomic script**, on **Redis's clock** (`TIME`, in microseconds), returning integers only (ADR-0004).
2. **[`RedisTokenBucket`](../../src/Flux.Gateway/RateLimiting/RedisTokenBucket.cs)** + **[`RedisConnection`](../../src/Flux.Gateway/RateLimiting/RedisConnection.cs)**:
   - one bucket per merchant and route group, shared by every gateway: the fix for E1;
   - one multiplexer per process, connecting in the background, so the gateway starts without Redis.
3. **[`ResilientRateLimiter`](../../src/Flux.Gateway/RateLimiting/ResilientRateLimiter.cs)**, a decorator (spec §9.1) built on Polly v8 (ADR-0010):
   - a 50 ms timeout;
   - a circuit breaker: ≥ 50% of ≥ 20 calls in 10 s → open 5 s → one trial call;
   - per-route policy (ADR-0005): payments **fail open** to a local `limit ÷ AssumedInstances` bucket with `X-RateLimit-Degraded: true`; refunds **fail closed** with `503 rate_limiter_unavailable` and `Retry-After: 5`.
   - The Redis client is set to **fail fast** instead of queueing while disconnected.
4. **Config** (all validated at startup, each error naming its key):
   - `RateLimiting:Redis:TimeoutMs` / `UseResilience`;
   - `RateLimiting:Fallback:AssumedInstances`;
   - `RateLimiting:FailurePolicy:{payments,refunds}`;
   - `ConnectionStrings:Redis`, required for `RedisTokenBucket`.
5. **Metrics:** `flux_ratelimiter_fallback_total{route_group,policy}` and `flux_ratelimiter_circuit_state` (spec §11.2).
6. **Compose:**
   - a `redis` service (7.4, no persistence, host port 6380);
   - shell switches `FLUX_REDIS_RESILIENCE`, `FLUX_REDIS_TIMEOUT_MS` and `FLUX_ASSUMED_INSTANCES`.
7. **Outage tooling** in [`bench/`](../../bench/README.md):
   - `run-outage.sh` breaks Redis on a schedule (`stop` or `pause`) and records the instants;
   - `redis-outage.js` sends steady traffic;
   - `analyze_outage.py` turns k6's per-request NDJSON into before/during/after tables and a per-second timeline.
8. **The experiments, run and written up:**
   - **[E3](../reports/E3-redis-outage-default-timeouts.md)**: with default client settings, requests hung for a median of **~5.5 s** and then got `500`s (88% of payments). After recovery, a well-behaved merchant got **138 + 30 false `429`s**, because the client's queue was replayed as one burst. Reproduced exactly in a second run.
   - **[P4](../reports/P4-redis-outage-resilient.md)**: with resilience, **100% of payments were served** (degraded) at normal latency, refunds got **`503` in ~1.1 ms**, and recovery was automatic in **4.1–4.6 s**. Spec N4 is met, with one stated caveat. P4 also **found a bug** (2 × `500` under `docker pause`), which was fixed and re-verified in three more runs.
9. **Tests: 135 in total, all passing, three runs in a row** (M4 had 118):
   - unit 47 (+4): which exceptions mean "Redis failed";
   - integration 78 (+13), all against a **real Redis 7.4** in Testcontainers:
     - the script's maths, its hash layout and TTL, refill by time-shifting the stored `ts`, and recovery after `SCRIPT FLUSH`;
     - **500 parallel requests from two "gateways" allowing exactly 100**;
     - two gateway hosts sharing a bucket;
     - fail open / fail closed with Redis dead and with a real Redis container stopped mid-test;
     - the breaker's state machine under `FakeTimeProvider`;
     - the gateway starting while Redis is down;
   - backend 10.

## Why this order (why *now*)
- **E1 gave the target, and M3 gave the parts.** The single-instance maths was proven in M3, so any wrong number here would point at the distribution code. M3's `InMemoryTokenBucket` is reused unchanged as the fail-open fallback.
- **Break before prove, again.** Without E3, "set a 50 ms timeout" is a rule of thumb. With it, there's a measured 5.5 s hang and a replay burst nobody predicted. That burst, and P4's cancellation bug, are the reason to run experiments rather than trust the design.
- **Resilience arrives with Redis, not after.** Redis is on every request's path from the moment it's switched on. Shipping the shared bucket without timeouts and a breaker would mean shipping E3 to production.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [Redis basics (for a rate limiter)](../concepts/redis-basics.md) | §12.8, §16, §20.3 |
| [Atomicity with Lua scripts](../concepts/lua-atomicity.md) | §11, §14, §16 |
| [Clock skew](../concepts/clock-skew.md) | §16 |
| [Timeouts and circuit breakers](../concepts/timeouts-and-circuit-breakers.md) | §17.1, §17.5, §17.7–8, §17.21 |
| [Fail open vs. fail closed](../concepts/fail-open-vs-fail-closed.md) | §17.7–8, §17.24, §25.3 |

New in the [glossary](../glossary.md): backlog (client), EVALSHA, fail fast, half-open, hash (Redis), lost update, multiplexer, thundering herd, Valkey.

## Decisions made during M5
| Decision | Why |
|----------|-----|
| [ADR-0010](../adr/0010-resilience-polly-fail-fast-client.md): Polly v8 + fail-fast client + "unrequested cancellation = failure" | It implements ADR-0005: Polly's options map one-to-one onto its numbers and run on `TimeProvider`. Fail-fast removes E3's hang and replay burst. The cancellation rule came from P4's bug. |
| The script stores `ts` in **microseconds** | No quantisation of refill to whole milliseconds; still within Lua's 53-bit precision. |
| The script is embedded as a real `.lua` file | It reads and diffs as Lua; `ScriptEvaluateAsync` handles `EVALSHA` and `NOSCRIPT`. |
| The limiter is created at startup (`GetRequiredService<IRateLimiter>()`) | The Redis connection starts immediately, and a broken limiter config fails at boot rather than on the first request. |
| Tests that count admissions against Redis use a seeded `test-slow` plan (0.001/s) | Redis's clock can't be faked. The first version of one test used "free" (10/s), and 415 ms of real time refilled 4 tokens. Refill itself is tested by shifting the stored `ts`. |
| The compose default algorithm stays `InMemoryTokenBucket` | So E1 and E2's documented commands still reproduce their reports. Redis experiments pass `FLUX_ALGORITHM=RedisTokenBucket` explicitly. |
| P4 uses `FLUX_ASSUMED_INSTANCES=1` | One gateway, so its fair local share is the whole plan. The ÷3 case is covered by an integration test, and the cluster by P1 (M6). |

## How to verify it yourself
Tests (Docker Desktop running; they start their own PostgreSQL and Redis):
```bash
dotnet test
```
**E3**, the broken baseline: default timeouts, no resilience.
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_REDIS_RESILIENCE=false FLUX_REDIS_TIMEOUT_MS=5000 FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
```
```bash
bench/run-outage.sh e3-stop stop
```
**P4**, resilient (the defaults):
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up -d gateway
```
```bash
bench/run-outage.sh p4-stop stop
```
```bash
bench/run-outage.sh p4-pause pause
```
Watch the breaker yourself, in a second terminal, during a P4 run:
```bash
watch -n1 "curl -s localhost:8080/metrics | grep -E 'circuit_state|fallback_total'"
```
**This session's results:**

| Run | Payments during outage | Refunds during outage | After |
|-----|------------------------|-----------------------|-------|
| E3 (×2) | 88% `500`, p50 ≈ 5.5 s | 90% `500`, p50 ≈ 5.5 s | 138 + 30–31 false `429`s |
| P4 stop (×2) | 100% `200` degraded, p99 ≈ 13 ms | `503`, p50 1.1 ms | normal in 4.1 s |
| P4 pause, after fix (×2) | 100% `200` degraded, max 74–80 ms | `503`, max 83–85 ms | normal in ~4.5 s |

## Exercises
1. Why must the read, refill, decide and write happen in *one* script? Write out the interleaving of two gateways that over-admits if they're four separate commands.
2. The script returns `retry_after_ms` as an integer. What would the client receive if it returned `0.07` seconds instead?
3. Suppose gateway A sent its own timestamp to the script, and A's clock ran 2 s ahead of B's. On a free plan (r = 10/s), how many free tokens could A's next request grant? What does `math.max(0, …)` protect against, and what *doesn't* it protect against?
4. In E3, why did a merchant sending only 50 of its allowed 100 payments/s get 138 `429`s *after* Redis came back?
5. In P4's `pause` runs, why were the first ~2 s slower (p99 60–68 ms) than the rest of the outage (p99 ~13 ms)? Which component made the difference?
6. Why does the breaker sit *outside* the timeout in the Polly pipeline? What would change if the order were reversed?
7. Why are refunds rejected with `503` rather than `429` when Redis is down? And why doesn't `/readyz` turn `503` too?
8. Three gateways fail open with `AssumedInstances = 3`. Someone scales to 6 replicas but forgets the setting. What happens to a free merchant's limit while Redis is down?

<details><summary>Answers</summary>

1. A: `HMGET` → 1 token. B: `HMGET` → 1 token. A: decides allow, `HSET tokens=0`. B: decides allow, `HSET tokens=0`. Two requests are admitted on one token, and the second write erases the evidence of the first (a *lost update*). Redis executes a script as one command, so no other command can run between its `HMGET` and its `HSET`.
2. `0`. Redis truncates Lua numbers to integers in replies, so `0.07` becomes `0`. A `Retry-After` computed from it would say "retry immediately" (or, after our minimum, `1`). Returning milliseconds as an integer (`70`) keeps the information.
3. Up to `2 s × 10/s = 20` tokens, but capped at B = 10. So A's next request could see a full bucket that B just drained. When *B* next runs, `now − ts` is negative (A wrote a timestamp in B's future). `math.max(0, …)` stops that from *removing* tokens, but it can't undo A's over-grant. The only real fix is one clock, which is why the script uses `TIME`.
4. The client queues commands while disconnected (its default backlog policy) and needed about 4 s to reconnect. When it did, every queued request reached Redis at once. Redis had restarted empty, so the bucket was a fresh 100 tokens, and the burst of roughly 240 queued payment requests used them up and then some. Requests arriving right after got `429`s until the bucket refilled. Fail-fast (no queue) removes the burst; P4 had none.
5. A paused Redis neither answers nor closes the connection, so each request waited out the **50 ms timeout**. The **circuit breaker** needed at least 20 calls, half of them failing, before it opened, about 1–2 s at 60 req/s. Once it was open, requests skipped Redis entirely and latency returned to normal.
6. With the breaker **outside**, the timeout fires *inside* it: the breaker sees a `TimeoutRejectedException` as the call's outcome and counts it as a failure. That's how a silently frozen Redis (`pause`) opens the breaker. **Reversed**, the timeout would cancel the breaker's token from outside. The breaker would see an `OperationCanceledException` on a token its *caller* cancelled, and our rule (`IsRedisFailure`: a cancellation the caller asked for is not a Redis failure) would not count it. The breaker would never open on a slow-but-not-erroring Redis, so every request would keep paying the full 50 ms for the whole outage.
7. `429` tells the client *it* exceeded a limit, but here the client did nothing wrong, and our limiter can't answer. `503 Service Unavailable` with `Retry-After` is the honest status, and clients know to back off. `/readyz` stays `200` because if readiness depended on Redis, every gateway would become unready at the same moment, the load balancer would remove them all, and payments (which can work degraded) would go down too.
8. Each gateway still enforces a third of the plan: refill 10/3 ≈ 3.33/s and burst `ceil(10/3) = 4`. Six gateways together allow about **2×** the plan (6 × 3.33/s ≈ 20/s) while degraded. ADR-0005's "revisit when" covers this: derive the divisor from live membership, or at least from the same value as `replicas` (an M7 task).
</details>

## What could go wrong
- **`bench/run-outage.sh` leaves Redis stopped or paused** if interrupted mid-run. Fix with `docker compose -f deploy/compose/docker-compose.yml unpause redis` or `start redis`.
- **Every integration test fails at once, in milliseconds.** Docker Desktop isn't running, so both fixtures fail to start. Start it and rerun.
- **A Redis test is flaky on a slow machine.** Real Redis time passes during the test. Tests that count admissions must use `PostgresFixture.SlowPlan`, and tests that need refill should shift the stored `ts` instead of waiting.
- **Port 6380 is in use.** The Compose Redis is published on `127.0.0.1:6380` for IDE runs. Change it in the compose file and in `appsettings.Development.json`.
- **E3's dropped iterations.** With 5 s responses, k6 needs hundreds of VUs. 240 iterations were dropped, so the outage was slightly worse than measured. That's documented, not hidden.
- **`FLUX_*` variables linger in your shell** and silently change the next experiment. Use `env | grep FLUX_` before each run.

## Next: M6 — Prove it
- **P1**: E1 again (3 gateways behind nginx) with `RedisTokenBucket`, within ±5% of 610 (N2).
- **P2**: quiet-shop's p95 while noisy-corp floods at 100× its limit (N3).
- **P3**: gateway overhead, measured two ways: direct vs. through the gateway, and `Server-Timing` (N1).
- The final README: diagram, results, and a résumé line with **measured** numbers only.
