# E3 — With default client timeouts, a Redis outage makes every request hang for ~5.5 s, then fail

- **Phase:** Break
- **Date:** 2026-10-16
- **Milestone:** M5
- **Code:** branch `m5-redis-resilience` (on top of M4, `37cda01`), with the M5 changes in this PR
- **Scripts:** [`bench/k6/redis-outage.js`](../../bench/k6/redis-outage.js), [`bench/run-outage.sh`](../../bench/run-outage.sh), [`bench/analyze_outage.py`](../../bench/analyze_outage.py)

## Hypothesis
With `RedisTokenBucket` and **no resilience**, using the client's default 5 s timeout and its default behaviour of *queueing* commands while disconnected, stopping Redis makes every request wait about 5 s and then fail with a `500`. The rate limiter becomes a single point of failure for *all* traffic. That includes payments, where ADR-0005 says we'd rather degrade.

## Setup
| | |
|---|---|
| Topology | k6 → one gateway (`single` profile) → backends (`Backend__LatencyMs=5`); Redis 7.4.11 in Compose |
| Gateway settings | `FLUX_ALGORITHM=RedisTokenBucket`, `FLUX_REDIS_RESILIENCE=false` (no `ResilientRateLimiter`, client's default backlog policy), `FLUX_REDIS_TIMEOUT_MS=5000` (the client's default) |
| Merchant | `quiet-shop`, plan **pro** (payments 100/s, refunds 20/s) |
| Load | k6 `constant-arrival-rate`, **50 payments/s + 10 refunds/s for 60 s**, well under the plan, so every non-`200` is caused by the outage. Up to 600 VUs. |
| Fault | `bench/run-outage.sh` runs `docker compose stop redis` at about +20 s and `start redis` about 21 s later, recording both instants |
| Analysis | each request is placed in a phase by the moment it was **sent** (k6's completion time minus its duration) |

## Machine
Lenovo 21SYS1A900, Intel Core Ultra 5 225H (14 cores), 14.9 GiB RAM, Windows 11 Pro 10.0.26100, Docker Desktop 28.4.0 on WSL2 (14 CPUs, 7.2 GiB visible to the VM), `grafana/k6:2.3.0` capped at 2 CPUs, `redis:7.4-alpine` (7.4.11), StackExchange.Redis 3.4.5, .NET 10.0.401.

## Exact commands
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_REDIS_RESILIENCE=false FLUX_REDIS_TIMEOUT_MS=5000 FLUX_ASSUMED_INSTANCES=1 \
  docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
curl -s -o /dev/null localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"   # warm-up
bench/run-outage.sh e3-stop stop
# second run (after `docker compose … restart gateway` and the same warm-up)
bench/run-outage.sh e3-stop-2 stop
```

## Results
**Run 1** (outage 20.8 s, 3,361 requests completed, **240 dropped iterations**)

| Phase | Route | Requests | Statuses | p50 | p99 | max |
|-------|-------|----------|----------|-----|-----|-----|
| before | payments | 966 | 200×966 | 10.26 ms | 13.36 ms | 34.86 ms |
| before | refunds | 194 | 200×194 | 10.13 ms | 11.76 ms | 105.38 ms |
| during | payments | 816 | 200×97 · **500×719** | **5,495.89 ms** | 5,990.85 ms | 6,004.96 ms |
| during | refunds | 198 | 200×19 · **500×179** | **5,453.80 ms** | 5,959.79 ms | 5,964.11 ms |
| after | payments | 988 | 200×850 · **429×138** | 10.70 ms | 3,209.56 ms | 3,390.44 ms |
| after | refunds | 199 | 200×169 · **429×30** | 10.56 ms | 3,242.92 ms | 3,343.11 ms |

**Run 2** (outage 21.0 s, 3,360 requests completed, **241 dropped iterations**)

| Phase | Route | Requests | Statuses | p50 | p99 | max |
|-------|-------|----------|----------|-----|-----|-----|
| before | payments | 959 | 200×959 | 8.17 ms | 13.99 ms | 19.62 ms |
| during | payments | 826 | 200×97 · **500×729** | **5,513.33 ms** | 5,998.67 ms | 6,013.33 ms |
| during | refunds | 201 | 200×20 · **500×181** | **5,482.08 ms** | 5,986.83 ms | 5,997.68 ms |
| after | payments | 983 | 200×845 · **429×138** | 8.21 ms | 3,367.34 ms | 3,547.03 ms |
| after | refunds | 199 | 200×168 · **429×31** | 8.34 ms | 3,376.08 ms | 3,476.28 ms |

**Timeline of run 1, per second from the break** (excerpt):

| t | payments | refunds | p99 (payments) |
|---|----------|---------|----------------|
| −1 s | 200×49 | 200×10 | 11.7 ms |
| +0 s | 200×18, 500×32 | 200×3, 500×7 | 5,934.9 ms |
| +1 … +18 s | **500 only** (25–48 per second) | **500 only** | ~5,950–6,005 ms |
| +19 s | 200×45, 500×4 | 200×9, 500×1 | 5,070.9 ms |
| +20 s | 200×47 | 200×10 | 4,107.0 ms |
| +21 s | 200×8, **429×39** | 200×1, **429×9** | 3,115.0 ms |
| +22 s | **429×47** | **429×10** | 2,082.4 ms |
| +23 s | **429×47** | **429×10** | 1,080.7 ms |
| +24 s | 200×45, 429×5 | 200×9, 429×1 | 81.4 ms |
| +25 s | 200×50 | 200×10 | 11.9 ms |

## What the numbers say
1. **Hypothesis confirmed:** while Redis was down, requests waited a median of **~5.5 s** and then failed with **`500 internal_error`**, payments and refunds alike. One dependency of the *rate limiter* took down *all* traffic. The 97 payments marked `200` in the "during" phase were sent in the first fraction of a second, before `docker stop` had taken effect, plus the queued tail (next point).
2. **The wait was spent in the client's queue, not on the network.** While disconnected, StackExchange.Redis's default backlog policy *holds* commands and either sends them on reconnect or fails them when the 5 s timeout expires. Requests sent in the last ~5 s of the outage were still queued when Redis came back, and completed as `200` after 4–5 s (t +19 to +20).
3. **Unexpected: the outage hurt a well-behaved merchant *after* it ended.** quiet-shop never exceeded its plan, yet it received **138 payments and 30 refunds `429`s** in the ~3 s after recovery, in both runs. The timeline shows why. Requests sent *after* Redis returned still waited up to ~3.5 s (shrinking each second), so the client needed about 4 s to reconnect, and everything queued in that time reached Redis **as one burst**. Redis restarted empty, so the bucket was a fresh 100 tokens, and the burst drained it. That's a *thundering herd* created by the queue itself. (This is our reading of the timeline; we didn't instrument the client's queue directly.)
4. **The load generator couldn't keep up either:** 240 dropped iterations. At 60 req/s with ~5.5 s responses, about 330 requests are in flight, and k6 couldn't allocate VUs fast enough. So the outage was slightly *worse* than measured, because some requests were never sent. That is coordinated omission in the wild, reported rather than hidden.

## Conclusion
Hypothesis confirmed, in both runs: with default client settings, a Redis outage turns into **~5.5 s hangs and `500`s for every request**, followed by a **burst of false `429`s** when Redis returns. The three measures in [ADR-0005](../adr/0005-per-route-failure-policy.md) each address one of these:
- a **50 ms timeout** bounds the wait;
- **fail-fast** instead of queueing removes both the 5 s hang and the replay burst;
- a **circuit breaker** stops trying, and the **per-route policy** replaces the `500` with a deliberate answer.

[P4](P4-redis-outage-resilient.md) reruns exactly this with them switched on.

## Caveats
- `docker stop` closes Redis's sockets, so the client *knows* it's disconnected. A network partition (packets silently dropped) is a different failure; P4 covers it with `docker pause`.
- One gateway. With three, each has its own queue and reconnects independently. The shape should be the same, but we didn't measure it.
