# P2 — A merchant flooding at 100× its limit doesn't slow a quiet merchant down

- **Phase:** Prove
- **Date:** 2026-10-16
- **Milestone:** M6
- **Code:** branch `m6-prove-it` (on top of M5, `304fba9`)
- **Script:** [`bench/k6/p2-noisy-neighbour.js`](../../bench/k6/p2-noisy-neighbour.js)

## Hypothesis (spec N3)
quiet-shop sends 20 req/s. Its **p95 latency rises by at most 10%** while noisy-corp (free: 10/s) floods the same route group at **1,000 req/s**, 100× its limit. The flood should be rejected cheaply at the gateway (`429`, never forwarded), so the backend and the quiet merchant shouldn't notice. Spec §15 also asks us to note the gateway's own CPU: rate limiting protects the *backends*, not the gateway.

## Setup
| | |
|---|---|
| Topology | k6 → one gateway (`single` profile) → payments (`Backend__LatencyMs=5`) |
| Algorithm | `RedisTokenBucket`, resilience on, `FLUX_ASSUMED_INSTANCES=1` |
| Load | quiet-shop (pro, 100/s): **20 req/s** for 60 s, plus, in the "flood" runs, noisy-corp **1,000 req/s** for the same 60 s, both on `GET /v1/payments/pay_1`, both `constant-arrival-rate` |
| Order | baseline, flood, baseline, flood, baseline, flood, then two control runs (below), on the same running gateway |
| CPU | `docker stats` sampled 20 times during runs 5 and 6 (100% = one core) |

## Machine
As for [P1](P1-shared-redis-bucket.md#machine). k6 is capped at 2 CPUs and shares the laptop with the system under test. At 1,020 req/s k6 itself used about 41% of a core.

## Exact commands
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up -d
# baseline
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e NOISY_RATE=0 -e LABEL=p2-baseline-1 /scripts/p2-noisy-neighbour.js
# flood
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e NOISY_RATE=1000 -e LABEL=p2-noisy-1 /scripts/p2-noisy-neighbour.js
# control: baseline with an unrelated container burning one core
docker run -d --rm --name flux-cpu-burner --cpus 1 alpine:3.20 sh -c 'while :; do :; done'
#   …baseline command with LABEL=p2-baseline-busy-cpu…; then: docker stop flux-cpu-burner
```

## Results
**quiet-shop's latency** (all 1,200–1,201 of its requests were `200` in every run; 0 dropped iterations everywhere):

| Run | noisy-corp | quiet p50 | **quiet p95** | quiet p99 | quiet max |
|-----|-----------|-----------|---------------|-----------|-----------|
| baseline 1 | 0 | 9.44 ms | **12.80 ms** | 15.37 ms | 203.54 ms (first run after start) |
| flood 1 | 1,000 req/s | 7.76 ms | **10.02 ms** | 10.65 ms | 87.39 ms |
| baseline 2 | 0 | 10.45 ms | **13.56 ms** | 14.43 ms | 17.66 ms |
| flood 2 | 1,000 req/s | 7.91 ms | **9.95 ms** | 10.57 ms | 30.32 ms |
| baseline 3 | 0 | 9.21 ms | **12.62 ms** | 13.77 ms | 14.52 ms |
| flood 3 | 1,000 req/s | 7.90 ms | **9.47 ms** | 10.38 ms | 15.19 ms |

**What happened to the flood** (each flood run, identical in all three): **200 × 609, 429 × 59,391–59,392.** That's exactly `B + r·T = 610` (minus the usual 1), and everything else was refused at the gateway.

**CPU, mean of 20 samples** (100% = one core):

| Container | baseline 3 | flood 3 |
|-----------|-----------|---------|
| gateway | 6% | **58%** (max 66%) |
| Redis | 2% | 9% |
| **payments backend** | 2% | **2%** |
| PostgreSQL | 0% | 1% |
| k6 | 6% | 41% |

**Controls** (quiet-shop alone, no flood):

| Run | Condition | quiet p50 | **quiet p95** | quiet p99 |
|-----|-----------|-----------|---------------|-----------|
| busy-CPU | an unrelated container burning 1 core (99.96%) | 8.71 ms | **10.51 ms** | 11.34 ms |
| baseline 4 | idle again, immediately after | 9.61 ms | **12.71 ms** | 14.58 ms |

## What the numbers say
1. **N3 is met.** quiet-shop's p95 did not rise during the flood. In all three pairs it *fell*, from 12.62–13.56 ms to 9.47–10.02 ms.
2. **The fall isn't Flux's doing, and the controls show it.**
   - With an unrelated process keeping one core busy and no flood at all, the idle baseline's p95 dropped by about the same amount (to 10.51 ms). The very next idle run went straight back to 12.71 ms.
   - **Our explanation** (supported by the control, not proven): on an idle laptop, cores sleep and clock down, so each of quiet-shop's lone requests pays a wake-up cost. Any steady background load keeps the cores awake.
   - So the honest statement is: **the flood added no measurable latency for quiet-shop**, and laptop power management is a larger effect than the noisy neighbour.
3. **The backend was fully protected.** The payments service's CPU stayed at 2% while 1,000 extra requests per second arrived at the gateway, because 99% of them were `429`s that were never forwarded. That's rate limiting doing exactly what it's for.
4. **The gateway paid for it.** Its CPU rose about 10× (6% → 58% of a core) to authenticate (from cache) and refuse about 990 req/s, and Redis rose to 9%. As spec §15 anticipated, rate limiting protects the backends, *not* the gateway itself. On this machine the gateway had plenty of headroom. At a large enough flood the gateway becomes the bottleneck, and protecting *it* needs something earlier: per-IP limits at the edge, or load shedding (§17.9).
5. **Isolation is exact.** noisy-corp got precisely its own 609 and not one more, whatever the rate. quiet-shop got all 1,200 of its requests. One merchant's bucket can't spend another's.

## Conclusion
Hypothesis confirmed: N3 is met. quiet-shop's p95 showed **no increase** under a flood at 100× noisy-corp's limit (three pairs). The flooding merchant got exactly its quota, and the backend's CPU didn't move. The apparent *improvement* was traced to laptop power management, by a control run with an unrelated CPU load. That's a reminder that a benchmark measures the whole machine, not just the code (see [benchmarking-methodology](../concepts/benchmarking-methodology.md)).

## Caveats
- One gateway. With three, the flood would be spread across them, and the per-gateway CPU cost divided accordingly.
- The latencies include a 5 ms artificial backend delay. The *differences* are what matter.
- The power-management explanation fits the control runs but wasn't confirmed with OS-level frequency counters.
