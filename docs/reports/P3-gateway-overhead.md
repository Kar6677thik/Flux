# P3 — The gateway adds about 0.3–0.7 ms at p50 and 1.1–1.6 ms at p99, at 500 req/s with Redis

- **Phase:** Prove
- **Date:** 2026-10-16
- **Milestone:** M6
- **Code:** branch `m6-prove-it` (on top of M5, `304fba9`)
- **Script:** [`bench/k6/p3-overhead.js`](../../bench/k6/p3-overhead.js)

## Hypothesis (spec N1)
At 500 req/s on the local machine, with the Redis limiter, the gateway's own overhead is **≤ 2 ms at p50 and ≤ 10 ms at p99**. Measured two independent ways that should roughly agree:
1. **Difference:** latency of k6 → gateway → backend, minus latency of k6 → backend directly, at the same rate.
2. **`Server-Timing`:** the gateway's own `gw;dur` (time inside the gateway, minus waiting on the backend) on every response.

## Setup
| | |
|---|---|
| Topology | **direct:** k6 → `payments:8080/payments/pay_1` (with the `X-Flux-Merchant-Id` header the gateway would add). **gateway:** k6 → `gateway:8080/v1/payments/pay_1` → payments. Both on the Compose network. |
| Gateway | one gateway, `RedisTokenBucket`, resilience on: the production path (correlation id, header stripping, route group, auth from cache, Redis check, YARP forward) |
| Load | k6 `constant-arrival-rate` **500 req/s for 60 s**, 100–400 VUs. For the gateway runs, 10 freshly created pro merchants (100/s each), rotated, so about 50/s each and nothing is rate-limited. |
| Backend | `Backend__LatencyMs=5`: every request includes a 5 ms artificial delay, identical in both paths |
| Order | direct, gateway, direct, gateway, on the same running stack (warm), straight after P2 |

## Machine
As for [P1](P1-shared-redis-bucket.md#machine): Intel Core Ultra 5 225H (14 cores), 14.9 GiB, Windows 11 / Docker Desktop 28.4.0 on WSL2, `grafana/k6:2.3.0` capped at 2 CPUs, Redis 7.4.11, .NET 10.0.401.

## Exact commands
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up -d
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e GATEWAYS=gateway -e TARGET=direct -e LABEL=p3-direct-1 /scripts/p3-overhead.js
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e GATEWAYS=gateway -e TARGET=gateway -e LABEL=p3-gateway-1 /scripts/p3-overhead.js
# …then p3-direct-2 and p3-gateway-2
curl -s localhost:8080/metrics | grep flux_ratelimiter_duration_seconds   # afterwards
```

## Results
Every run: 30,001 requests, **all `200`**, **0 dropped iterations**.

**Client-side latency (ms):**

| Run | p50 | p90 | p95 | p99 | max |
|-----|-----|-----|-----|-----|-----|
| direct 1 | 7.288 | 8.677 | 8.903 | 9.448 | 34.853 |
| gateway 1 | 8.000 | 9.793 | 10.105 | 11.020 | 20.394 |
| direct 2 | 7.548 | 8.977 | 9.222 | 9.885 | 36.098 |
| gateway 2 | 8.013 | 9.652 | 10.203 | 10.968 | 18.780 |

**Method 1, difference (gateway − direct, paired by order):**

| Pair | Δ p50 | Δ p95 | Δ p99 |
|------|-------|-------|-------|
| 1 | **0.71 ms** | 1.20 ms | **1.57 ms** |
| 2 | **0.47 ms** | 0.98 ms | **1.08 ms** |

**Method 2, `Server-Timing` (ms), on every gateway response:**

| Run | `gw` p50 | `gw` p90 | `gw` p95 | `gw` p99 | `gw` max | `upstream` p50 | `upstream` p99 |
|-----|----------|----------|----------|----------|----------|----------------|----------------|
| gateway 1 | **0.314** | 0.597 | 0.752 | **1.125** | 10.437 | 7.222 | 9.718 |
| gateway 2 | **0.300** | 0.778 | 1.033 | **1.503** | 8.366 | 6.987 | 9.357 |

**Where the gateway's time goes**, from its own histograms over all 249,610 decisions since start (P2 + P3):

| | Mean | Distribution |
|---|------|--------------|
| `flux_ratelimiter_duration_seconds{algorithm="RedisTokenBucket"}` | **0.316 ms** | ≤ 0.25 ms: 49% · ≤ 0.5 ms: 89% · ≤ 1 ms: 98% · ≤ 2.5 ms: 99.9% |
| `flux_gateway_overhead_seconds` | **0.365 ms** | (sum 91.2 s ÷ 249,610) |

## What the numbers say
1. **N1 is met by both methods, with a wide margin:**
   - p50: **0.30–0.71 ms** against a 2 ms target;
   - p99: **1.08–1.57 ms** against 10 ms.
2. **The two methods agree in size but not exactly, for good reasons:**
   - The difference method (0.47–0.71 ms) is larger than `gw` (0.30–0.31 ms) at p50. It also includes things `Server-Timing` can't see: the extra network hop k6 → gateway and Kestrel parsing the request before our middleware starts the clock.
   - A difference *of percentiles* also isn't the percentile *of differences* ([latency-percentiles](../concepts/latency-percentiles.md)). That's why we measured two ways and report both.
3. **The Redis check is most of the gateway's own time:** 0.316 ms of the 0.365 ms mean, about 86%. Everything else (correlation, header stripping, cached auth, routing, YARP) adds roughly 0.05 ms. This is the number ADR-0004's "revisit when" asks about. Even so, it's well inside the budget. If it ever weren't, the next step is local token batching.
4. **Tails come from the machine, not the code path.** Both paths have similar `max` outliers (18–36 ms), present even *without* the gateway. That's scheduling on a shared, virtualised laptop. The gateway's `gw` max of 8–10 ms falls in the same category.

## Conclusion
Hypothesis confirmed: at 500 req/s with the Redis limiter, the gateway adds **0.30–0.71 ms at p50** and **1.08–1.57 ms at p99**, by two independent methods. That's well inside N1's 2 ms / 10 ms. About 86% of the gateway's own time is the Redis round trip.

## Caveats
- One machine, everything virtualised in WSL2, k6 sharing CPUs (capped at 2). Absolute numbers would differ on real hardware; the comparisons are what's valid.
- Two pairs of runs. The difference between pairs (0.71 vs. 0.47 ms at p50) shows the run-to-run noise is about the same size as the overhead itself. The conclusion (well under 2 ms) is robust to that; a precise figure like "0.6 ms" would not be.
- `Server-Timing` stops at the response's first byte (see [metrics-and-cardinality](../concepts/metrics-and-cardinality.md)). That's fine for these small bodies.
