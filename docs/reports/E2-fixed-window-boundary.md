# E2 — A fixed window allows twice the quota across its boundary

- **Phase:** Break
- **Date:** 2026-10-15
- **Milestone:** M4
- **Code:** branch `m4-break-it` (on top of M3, `5484d3a`), with the uncommitted M4 changes that are in this PR
- **Script:** [`bench/k6/e2-boundary.js`](../../bench/k6/e2-boundary.js)

## Hypothesis
`InMemoryFixedWindow` with `W = 10 s` gives `noisy-corp` (free: 10/s) a quota of `r × W = 100` per window. Windows are calendar slots aligned to the epoch. If a client sends 100 requests just before a boundary and 100 just after, all **200** are allowed within a few hundred milliseconds. That's 2× the per-window quota, and about **100×** the plan's stated 10/s for that moment. A token bucket on the same plan should allow only its burst `B = 10`, plus a couple of refilled tokens.

## Setup
| | |
|---|---|
| Topology | k6 → one gateway (`single` profile) → payments backend (`Backend__LatencyMs=5`) |
| Algorithms | `InMemoryFixedWindow` (W = 10 s), then `InMemoryTokenBucket` on the same script, as the control |
| Merchant | `noisy-corp`, plan **free** (payments `r = 10/s`, `B = 10`) |
| Load | 1 VU, 1 iteration: batch of **100 parallel** `GET /v1/payments/pay_1` starting **150 ms before** a 10 s boundary, then 100 more starting **50 ms after** it (`batch` and `batchPerHost` raised to 100) |
| Clock | k6 and the gateway run in the same Docker VM and read the same kernel clock, so they agree on when the boundary is |
| Switching | `FLUX_ALGORITHM=<name> docker compose … up -d gateway` recreates the gateway with the other algorithm. Same code, same image, only config (the Strategy seam from M3). |

## Machine
Same as [E1](E1-per-instance-buckets.md#machine): Intel Core Ultra 5 225H (14 cores), 14.9 GiB, Windows 11 Pro 10.0.26100, Docker Desktop 28.4.0 / WSL2, `grafana/k6:2.3.0` (2 CPUs), .NET 10.0.401.

## Exact commands
```bash
FLUX_ALGORITHM=InMemoryFixedWindow docker compose -f deploy/compose/docker-compose.yml --profile single up -d gateway
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e2-fixed-window-run1 /scripts/e2-boundary.js
#   …run2, run3, with `sleep 11` between them so each uses a fresh pair of windows

FLUX_ALGORITHM=InMemoryTokenBucket docker compose -f deploy/compose/docker-compose.yml --profile single up -d gateway
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e2-token-bucket-run1 /scripts/e2-boundary.js
```
(Git Bash: prefix the `run` commands with `MSYS_NO_PATHCONV=1`.)

## Results
"First batch ends" is measured from the boundary: negative means the whole first batch finished before it.

| Algorithm | Run | Allowed, 1st batch | Allowed, 2nd batch | **Total of 200** | 1st batch ends | Whole burst |
|-----------|-----|-------------------|-------------------|------------------|----------------|-------------|
| Fixed window | 1 (cold gateway) | 100 | 15 | **115** | +111 ms | 285 ms |
| Fixed window | 2 | 100 | 100 | **200** | −76 ms | 232 ms |
| Fixed window | 3 | 100 | 100 | **200** | −87 ms | 237 ms |
| Token bucket | 1 (cold gateway) | 10 | 0 | **10** | +65 ms | 218 ms |
| Token bucket | 2 | 10 | 1 | **11** | −119 ms | 211 ms |
| Token bucket | 3 | 10 | 2 | **12** | −102 ms | 228 ms |

## What the numbers say
1. **Fixed window, warm (runs 2 and 3): 200 of 200 allowed in about 235 ms.** The plan says 10/s. Over that quarter-second the client got the equivalent of about 850 req/s. Each window individually respected its quota of 100; the boundary between them is what got exploited.
2. **Token bucket on the same script: 10–12 allowed.** That's the burst of 10 plus whatever refilled while the batches were in flight (about 0.2 s × 10/s ≈ 2). The bucket has no boundary to aim at.
3. **The cold runs are the interesting exceptions:**
   - **Fixed window, run 1 (115):** the first batch on a freshly started gateway took 261 ms, so it finished 111 ms *after* the boundary. Part of the first batch was therefore counted in the new window. Only 15 slots were left in it when the second batch arrived.
   - **Token bucket, run 1:** 0 in the second batch, where warm runs got 1–2.

   **Our explanation, not measured directly:** on a cold gateway, every one of the first 100 parallel requests misses the empty API-key cache and queries PostgreSQL at once. That's the *cache stampede* described in M2's caching note. So the first batch reached the limiter later and all together, and almost no refill time was left before the second batch. Either way, the warm runs are the ones that represent the attack: a real attacker warms up first.
4. **Timing precision matters for the attacker.** The burst only doubles if *both* batches land inside their intended windows. That's easy here: the windows are published (epoch-aligned, 10 s) and `X-RateLimit-Reset` even says when the next one starts.

## Conclusion
Hypothesis confirmed for the warm runs: **2× the window quota (200) within about 235 ms**, against **10–12** for a token bucket under identical load. Any limiter with calendar-aligned windows has this flaw, and that's why the token bucket is Flux's production algorithm ([ADR-0002](../adr/0002-token-bucket.md)). `InMemoryFixedWindow` remains in the code only to reproduce this experiment.

## Caveats
- The burst size is set by the quota *per window* (`r × W`). A longer window (say 60 s, quota 600) makes the absolute burst bigger, not smaller.
- A *sliding window counter* (a weighted blend of the current and previous windows) mostly removes the boundary effect at almost the same cost. It's listed as a stretch goal.
- Unconfirmed: the cold-start explanation in point 3. Checking it would mean counting database queries during the first batch, which isn't worth doing for this experiment.
