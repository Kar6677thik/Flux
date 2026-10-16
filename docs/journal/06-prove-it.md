# Journal 06 — Prove it (M6)

- **Date:** 2026-10-16
- **Milestone:** M6
- **Branch:** `m6-prove-it` (built on `m5-redis-resilience`; the PR targets `main`)

## What we did
No gateway code changed in this milestone. M6 measures what M1–M5 built.
1. **Two new k6 scripts** in [`bench/k6/`](../../bench/k6/):
   - `p2-noisy-neighbour.js`: a quiet merchant at 20 req/s, optionally with a flooding one;
   - `p3-overhead.js`: 500 req/s, direct to the backend or through the gateway. It parses `Server-Timing` and creates its own 10 pro merchants through the admin API, so nothing is rate-limited.

   P1 reuses E1's script unchanged; only the algorithm differs.
2. **[P1](../reports/P1-shared-redis-bucket.md):** 3 gateways with the Redis bucket allowed **608 / 609 / 609 of 610**, against E1's 1,822–1,825. **N2 met** (≤ 0.3% error, target ±5%).
3. **[P2](../reports/P2-noisy-neighbour.md):** noisy-corp at **1,000 req/s** (100× its limit) next to quiet-shop at 20 req/s, three interleaved pairs.
   - quiet-shop's p95 showed **no increase**. It actually fell; two control runs traced that to laptop power management.
   - The flooder got exactly 609, and the backend's CPU stayed at 2%. **N3 met.**
4. **[P3](../reports/P3-gateway-overhead.md):** at 500 req/s with Redis, the gateway adds **0.30–0.71 ms at p50 and 1.08–1.57 ms at p99**, measured two independent ways. About 86% of the gateway's own time is the Redis check. **N1 met.**
5. **The final [README](../../README.md):** architecture diagram, a results table where every claim links to its report, how to run it, and a résumé line made only of measured numbers.
6. **Spec §13 updated:** N1–N4 are all measured, with links.

## Why this order (why *now*)
- **Proof needs the finished system.** P1 needs the Redis bucket, P2 needs isolation from end to end, and P3 needs the whole hot path (auth cache, Redis check, YARP) in its final form. Run earlier, these would have measured an unfinished gateway.
- **Before deployment, not after.** M7 changes the environment (k3s, a different machine). These laptop numbers, with recorded methodology, are the baseline that any server rerun is compared against. Deploying first would mix "did the code regress?" with "is the server different?".
- **The README comes last on purpose.** A résumé line written before measuring is a guess. This one quotes P1, P3 and P4.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [Latency percentiles](../concepts/latency-percentiles.md) | §27, §24 |
| [Noisy-neighbour isolation](../concepts/noisy-neighbour-isolation.md) | §20.15–16, §20.18, §17.9 |
| [Benchmarking methodology](../concepts/benchmarking-methodology.md) | §27, §23.14, §1.11 |

New in the [glossary](../glossary.md): baseline (benchmark), control run, load shedding, tail latency, warm-up.

## Decisions made during M6
| Decision | Why |
|----------|-----|
| P1 reuses E1's script byte for byte | Before and after differ in exactly one variable, the algorithm, so the 2.99× → 1.00× comparison is clean. |
| P2 interleaves baseline and flood runs (×3) on one running gateway | Alternating cancels slow drift (thermals, caches) that back-to-back blocks would confound with the effect. |
| P2 adds two **control** runs (an unrelated CPU burner, then idle) | The flood made the quiet merchant *faster*, a surprise. A surprise in a benchmark is usually the setup, and the control showed it was CPU power states. |
| P3 creates 10 pro merchants instead of adding an "unlimited" plan | 500 req/s on one merchant would be rate-limited (pro is 100/s), and a new plan would mean a migration just for a benchmark. Ten merchants at about 50/s each go through the full path, Redis check included, with nothing refused. |
| P3 reports two methods side by side | They measure slightly different things (the difference includes the extra network hop and Kestrel's parsing). Reporting both, and explaining the gap, is more honest than picking the nicer one. |
| `FLUSHALL` between P1 runs, but no gateway restart | Each run starts from a full bucket. The cost is cumulative per-gateway counters, so the report derives per-run values explicitly. |

## How to verify it yourself
**P1** (Git Bash: keep `MSYS_NO_PATHCONV=1`):
```bash
FLUX_ALGORITHM=RedisTokenBucket docker compose -f deploy/compose/docker-compose.yml --profile cluster up --build -d
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e LABEL=p1 /scripts/e1-instances.js
```
**P2 and P3** run against one gateway:
```bash
docker compose -f deploy/compose/docker-compose.yml --profile cluster down
```
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up -d
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e NOISY_RATE=1000 -e LABEL=p2-noisy /scripts/p2-noisy-neighbour.js
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e GATEWAYS=gateway -e TARGET=gateway -e LABEL=p3-gateway /scripts/p3-overhead.js
```
Run P2 again with `NOISY_RATE=0`, and P3 with `TARGET=direct`, for the comparisons.

**This session's results:**

| Experiment | Result | Target |
|------------|--------|--------|
| P1 (×3) | allowed 608 / 609 / 609 of 610 | ±5% |
| P2 (×3 pairs) | quiet p95: alone 12.62–13.56 ms, under flood 9.47–10.02 ms; flooder 609 allowed / ~59,392 `429`; backend CPU 2% → 2% | ≤ +10% |
| P2 controls | idle + CPU burner: p95 10.51 ms; idle again: 12.71 ms | n/a |
| P3 (×2 pairs) | Δ p50 0.71 / 0.47 ms, Δ p99 1.57 / 1.08 ms; `Server-Timing gw` p50 0.31 / 0.30 ms, p99 1.13 / 1.50 ms | ≤ 2 / ≤ 10 ms |

## Exercises
1. P1 allowed 609 where E1 allowed 1,825, with the same script and setup. What's the single variable that changed, and why does that make the comparison trustworthy?
2. In P1 the three gateways' shares varied (210/199/199, then 188/246/175) while the sum stayed at 608–609. Why does the split vary but not the sum?
3. P2's quiet merchant got *faster* under the flood. Name two explanations that are *not* "Flux is magic". How did the control runs choose between them?
4. P3's difference method gives a larger p50 (0.47–0.71 ms) than `Server-Timing` (0.30 ms). What does the difference method include that `Server-Timing` can't see?
5. Why can't you compute "the p99 overhead" as `p99(gateway) − p99(direct)`, strictly speaking? What is that subtraction an estimate *of*?
6. P2 showed the gateway's CPU rising about 10× during the flood. If the flood were 50,000 req/s, what would fail first, and which Field Guide pattern addresses it?
7. P3 says about 86% of the gateway's own time is the Redis check. If you had to halve gateway overhead, what would you try first, and what would it cost?

<details><summary>Answers</summary>

1. Only `RateLimiting:Algorithm` changed, from `InMemoryTokenBucket` to `RedisTokenBucket`. The script, rate, duration, merchant, topology and machine were the same. With one variable changed, the difference in the result (2.99× → 1.00×) can only come from that variable.
2. nginx round-robins requests, but which gateway's request happens to arrive just after a token refills, and so gets it, is effectively random. The sum is fixed by the one shared bucket: `B + r·T`, minus the last partial token. However the three split the traffic, they spend from the same 610.
3. (a) **CPU power management:** an idle laptop parks and downclocks cores, so lone requests pay wake-up latency, and any steady load keeps them awake. (b) Warmer **caches or connection pools** under load. The control kept all Flux traffic identical (quiet merchant only) and added an *unrelated* CPU burner. The p95 dropped just as it did under the flood, then rose again when the burner stopped. That supports (a), because the burner warms none of Flux's caches.
4. The extra network hop k6 → gateway (the direct path goes k6 → backend in one hop, the gateway path in two), plus the time Kestrel spends accepting and parsing the request before our first middleware starts the clock, and any time after the response's first byte. `Server-Timing` only measures inside our middleware pipeline.
5. Percentiles don't subtract: the slowest 1% of gateway requests aren't the same requests as the slowest 1% of direct ones, and the two distributions have different shapes. `p99(a) − p99(b)` estimates how far the *tail shifted*. It's a fair size estimate, but not the 99th percentile of per-request overhead. Measuring per request (method 2) gets closer.
6. The gateway's CPU. Each refused request still costs a cached auth lookup, a Redis call and a `429` response. At some rate the gateway saturates and *everyone's* latency rises, the quiet merchant's included: the noisy neighbour wins after all, one layer up. The patterns: **load shedding** (§17.9, refuse cheaply under overload before per-request work), **per-IP or per-connection limits at the edge**, and **capacity headroom** (§20.22, more replicas).
7. **Local token batching:** each gateway reserves a small batch of tokens from Redis (say 10) and spends them locally, calling Redis once per batch. That cuts Redis calls by about the batch size. The cost is precision: a merchant can exceed its limit by up to `batch × instances` at the edges, and tokens reserved by a gateway that dies are lost. ADR-0004 names this as the next step if Redis round trips ever dominate. They're 86% of a ~0.37 ms overhead, so that day isn't today.
</details>

## What could go wrong
- **A result that looks too good.** P2's quiet merchant got faster under a flood. Run a control before believing or reporting it. The P2 report shows how.
- **Comparing runs from different machine states:** different power plans, a laptop on battery, other apps busy. Keep conditions identical and interleave runs.
- **Reading cumulative counters as per-run values.** Gateway `/metrics` counters only reset on restart. P1's per-gateway numbers are derived by subtracting the previous run.
- **P3 merchants accumulate.** Every P3 gateway run creates 10 new merchants. That's harmless locally; `docker compose … down -v` resets the database.
- **`dropped_iterations` > 0** invalidates a latency run (coordinated omission). Every M6 run had 0. Check yours.

## Next: M7 — Deploy
- k3s manifests (Kustomize) for the `flux` namespace, a NetworkPolicy, and the migration Job before the rollout.
- `deploy.yml`: build → push images to GHCR → Tailscale → `flux-deploy` sudo helper → smoke test.
- **Steps only you can do** (spec §17): GitHub secrets, a deploy key, installing the sudo helper, Tailscale Serve on `:8443`. M7 will list each with exact commands.
