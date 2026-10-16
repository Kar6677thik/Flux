# P1 — With a shared Redis bucket, 3 gateways enforce the limit to within 0.3%

- **Phase:** Prove
- **Date:** 2026-10-16
- **Milestone:** M6
- **Code:** branch `m6-prove-it` (on top of M5, `304fba9`); no gateway code changed for this experiment
- **Script:** [`bench/k6/e1-instances.js`](../../bench/k6/e1-instances.js), the same script as [E1](E1-per-instance-buckets.md)

## Hypothesis (spec N2)
Rerun E1 exactly (3 gateways behind nginx, noisy-corp offering 100 req/s for 60 s), but with `RedisTokenBucket` instead of in-memory buckets. Allowed requests should fall from E1's ~1,825 (2.99×) to within **±5% of `B + r·T = 610`**, i.e. 580–640, because all three gateways now spend from one bucket.

## Setup
| | |
|---|---|
| Topology | k6 → nginx (L7 round-robin) → `gateway-1/2/3` → payments; Redis 7.4.11 shared by all three |
| Algorithm | `FLUX_ALGORITHM=RedisTokenBucket`, resilience on (defaults), `AssumedInstances = 3` |
| Merchant / load | noisy-corp (free: `r = 10/s`, `B = 10`), k6 `constant-arrival-rate` 100 req/s for 60 s, `GET /v1/payments/pay_1` |
| Resets | `redis-cli FLUSHALL` before each run, so every run starts from a full bucket. Gateways were *not* restarted between runs, so their `/metrics` counters are cumulative (per-run values derived below). |

## Machine
Lenovo 21SYS1A900, Intel Core Ultra 5 225H (14 cores), 14.9 GiB, Windows 11 Pro 10.0.26100, Docker Desktop 28.4.0 / WSL2 (14 CPUs, 7.2 GiB in the VM), `grafana/k6:2.3.0` capped at 2 CPUs, `nginx:1.30-alpine`, Redis 7.4.11, .NET 10.0.401.

## Exact commands
```bash
FLUX_ALGORITHM=RedisTokenBucket docker compose -f deploy/compose/docker-compose.yml --profile cluster up --build -d
docker compose -f deploy/compose/docker-compose.yml exec -T redis redis-cli FLUSHALL
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e LABEL=p1-cluster-redis-run1 /scripts/e1-instances.js
# runs 2 and 3: FLUSHALL again, LABEL=p1-cluster-redis-run2 / -run3
```

## Results
| Run | Sent | **Allowed** | Limited | Other | Allowed ÷ 610 | Error vs. 610 | Per gateway (this run) | k6 dropped | p50 / p99 |
|-----|------|-------------|---------|-------|---------------|---------------|------------------------|-----------|-----------|
| 1 | 6001 | **608** | 5393 | 0 | 1.00× | −0.3% | 210 · 199 · 199 | 0 | 2.11 / 13.01 ms |
| 2 | 6001 | **609** | 5392 | 0 | 1.00× | −0.2% | 188 · 246 · 175 | 0 | 2.17 / 12.53 ms |
| 3 | 6001 | **609** | 5392 | 0 | 1.00× | −0.2% | 204 · 203 · 202 | 0 | 2.03 / 10.17 ms |

Per-gateway values for runs 2 and 3 are the cumulative `/metrics` counts minus the previous run's (cumulative: run 2 = 398 · 445 · 374, run 3 = 602 · 648 · 576). Each run's three values add up to k6's allowed count.

**Before and after, side by side:**

| | E1 (in-memory, 3 gateways) | **P1 (Redis, 3 gateways)** | E1 baseline (1 gateway) |
|---|---|---|---|
| Allowed | 1,822 / 1,825 / 1,825 | **608 / 609 / 609** | 609 / 609 |
| ÷ bound | 2.99× | **1.00×** | 1.00× |

## What the numbers say
1. **N2 is met with a wide margin:** 608–609 of 610, an error of 0.2–0.3% against a ±5% target. Three gateways now behave exactly like one, which is the point of shared state.
2. **Each gateway now admits about a third**, not a full 610. In E1 each allowed 607–609 independently. Here they *share* 608–609 between them: 210 + 199 + 199, 188 + 246 + 175, and so on. The split varies run to run (it depends on which gateway's request arrives just after a refill), but the sum doesn't.
3. **The atomic script has no measurable leak.** Over-admission from a race would show as *more* than 610. We're 1–2 under, and that's the same "last fraction of a refill is never used" effect as E1's single-gateway baseline.
4. **The cost is visible but small:** p50 rose from 1.4–1.6 ms (E1, in memory) to 2.0–2.2 ms, because every decision is now a Redis round trip. [P3](P3-gateway-overhead.md) measures that round trip on its own (about 0.32 ms mean).

## Conclusion
Hypothesis confirmed: a single Redis bucket, updated by one atomic Lua script on Redis's clock, turns E1's **2.99×** into **1.00×** (608–609 of 610) across three gateways and three runs. The bug E1 demonstrated is fixed, and the fix is measured.
