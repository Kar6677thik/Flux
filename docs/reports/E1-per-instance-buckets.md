# E1 — Per-instance buckets let a merchant through at 3× its limit

- **Phase:** Break
- **Date:** 2026-10-15
- **Milestone:** M4
- **Code:** branch `m4-break-it` (on top of M3, `5484d3a`), with the uncommitted M4 changes that are in this PR
- **Script:** [`bench/k6/e1-instances.js`](../../bench/k6/e1-instances.js)

## Hypothesis
With three gateway instances behind a round-robin load balancer, each running `InMemoryTokenBucket`, every instance keeps its own bucket for `noisy-corp`. The merchant therefore gets about **3×** its quota. A single, correctly shared limiter would allow at most `B + r·T = 10 + 10 × 60 = 610` requests in 60 s, so we expect about 1,830.

## Setup
| | |
|---|---|
| Topology | k6 → nginx (L7 round-robin, keep-alive to upstreams) → `gateway-1/2/3` → payments backend (`Backend__LatencyMs=5`) |
| Algorithm | `InMemoryTokenBucket` (the default) |
| Merchant | `noisy-corp`, plan **free**: payments `r = 10/s`, `B = 10` |
| Load | k6 `constant-arrival-rate`, **100 req/s for 60 s**, `GET /v1/payments/pay_1`, 50–200 VUs |
| Baseline | the same load against **one** gateway (`single` profile), no nginx |
| Resets | gateways restarted before each run, so every run starts with full, fresh buckets |
| Counting | k6 counts `200` vs `429`. After the run, each gateway's own `/metrics` is scraped for its `flux_requests_total{route_group="payments",outcome="allowed"}` |

## Machine
| | |
|---|---|
| Laptop | Lenovo 21SYS1A900, Intel Core Ultra 5 225H (14 cores), 14.9 GiB RAM |
| OS | Windows 11 Pro 10.0.26100 |
| Containers | Docker Desktop 28.4.0 on WSL2 (kernel 6.6.87.2), VM sees 14 CPUs and 7.2 GiB |
| Images | `grafana/k6:2.3.0` (capped at 2 CPUs), `nginx:1.30-alpine`, `postgres:17-alpine`, gateway and backend on `mcr.microsoft.com/dotnet/aspnet:10.0` |
| SDK | .NET 10.0.401 |

k6 shares the laptop with everything it measures. That doesn't matter for this experiment, which counts admissions rather than timing them.

## Exact commands
```bash
# cluster: 3 gateways behind nginx
docker compose -f deploy/compose/docker-compose.yml --profile cluster up --build -d
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet /scripts/e1-instances.js
# repeat runs (restart = fresh in-memory buckets)
docker compose -f deploy/compose/docker-compose.yml --profile cluster restart gateway-1 gateway-2 gateway-3
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e LABEL=e1-cluster-run2 /scripts/e1-instances.js

# baseline: one gateway
docker compose -f deploy/compose/docker-compose.yml --profile cluster down
docker compose -f deploy/compose/docker-compose.yml --profile single up -d
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e1-single-run1 /scripts/e1-instances.js
```
In Git Bash, prefix the `run` commands with `MSYS_NO_PATHCONV=1`, or `/scripts/…` gets rewritten into a Windows path.

## Results
| Run | Gateways | Sent | Allowed | Limited | Other | Allowed ÷ 610 | Allowed per gateway (`/metrics`) | k6 dropped | p50 / p99 latency |
|-----|----------|------|---------|---------|-------|---------------|-------------------------------|-----------|-------------------|
| cluster 1 | 3 | 6001 | **1822** | 4179 | 0 | **2.99×** | 608 · 607 · 607 | 0 | 1.44 / 12.89 ms |
| cluster 2 | 3 | 6001 | **1825** | 4176 | 0 | **2.99×** | 608 · 608 · 609 | 0 | 1.49 / 11.85 ms |
| cluster 3 | 3 | 6001 | **1825** | 4176 | 0 | **2.99×** | 608 · 609 · 608 | 0 | 1.61 / 12.58 ms |
| single 1 | 1 | 6000 | **609** | 5391 | 0 | **1.00×** | 609 | 0 | 0.95 / 11.24 ms |
| single 2 | 1 | 6001 | **609** | 5392 | 0 | **1.00×** | 609 | 0 | 0.97 / 10.33 ms |

(The latency column is end to end as k6 sees it, *including* the 5 ms backend delay only for the allowed requests. Most requests here are fast `429`s. It's recorded for completeness; latency is P3's subject, not E1's.)

## What the numbers say
1. **The bug is exactly 3×, not "roughly".** Each gateway allowed 607–609, almost exactly the single-limiter bound of 610. Each one is individually correct. There are just three of them, and nginx gave each a third of the traffic (about 33 req/s, still well above 10/s), so each kept its own full allowance.
2. **The algorithm isn't the problem.** One gateway with the same code allowed 609 of 610, the bound minus at most one partially refilled token.
3. **The per-gateway sum equals what the client saw** (608 + 607 + 607 = 1,822). The metrics and the client agree, so `/metrics` can be trusted to diagnose this in production: a merchant's allowed rate summed across instances exceeds the plan.
4. **No dropped iterations**, so k6 really offered 100 req/s throughout. The counts aren't distorted by a load generator falling behind.

## Conclusion
Hypothesis confirmed: **2.99×** the quota across three runs, against **1.00×** for a single instance. Per-instance state can't enforce a per-merchant limit once traffic is spread over several instances, and the error grows linearly with the instance count. That's the design reason for M5's shared Redis bucket. Experiment P1 reruns exactly this setup against `RedisTokenBucket` and must land within ±5% of 610 (spec N2).

## Caveats
- Round-robin splits traffic almost perfectly evenly, so each instance's bucket saturates, which is the worst case. With sticky or uneven balancing, the multiple would be between 1× and 3×. That would make the bug *intermittent*, which is worse.
- On k3s, a Service balances connections (L4), not requests. One keep-alive connection from k6 could stick to one pod and show 1×, hiding the bug (spec §21). That's why this runs behind nginx locally.
