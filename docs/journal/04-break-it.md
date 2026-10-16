# Journal 04 — Break it (M4)

- **Date:** 2026-10-15/16
- **Milestone:** M4
- **Branch:** `m4-break-it` (built on top of `m3-token-bucket`; the PR targets `main`. See "What could go wrong" for why)

## What we did
1. **[`InMemoryFixedWindow`](../../src/Flux.Gateway/RateLimiting/InMemoryFixedWindow.cs)**: the deliberately flawed algorithm (spec §9.3).
   - It counts per epoch-aligned window, with quota `r × W`.
   - It's selected purely by config (`RateLimiting:Algorithm`): no other code changed, which is the Strategy seam from M3 paying off.
   - `RateLimitDecision` now carries its own `Limit`, because a fixed window's limit is the per-window quota (100), not a bucket capacity.
2. **Config validation**: `RateLimiting:Algorithm` must be a known name and `RateLimiting:FixedWindow:WindowSeconds` must be 1–3600, or the gateway refuses to start.
3. **The Compose `cluster` profile**: `gateway-1/2/3` behind **nginx** ([`nginx.conf`](../../deploy/compose/nginx.conf)), round-robin per request, with keep-alive to the gateways.
4. **A one-shot `migrate` service**:
   - the gateway image gained a `--migrate-only` mode (migrate, seed, exit);
   - every gateway waits for it to finish, so three gateways never race to migrate one database;
   - it's also the only service that builds the gateway image (the M2 race lesson, applied again).
5. **The `bench` profile**: k6 2.3.0 in Docker, capped at 2 CPUs, with scripts in [`bench/k6/`](../../bench/k6/) and a [`bench/README.md`](../../bench/README.md).
6. **The experiments, run and written up:**
   - **[E1](../reports/E1-per-instance-buckets.md)**: 3 gateways allowed **1,822–1,825** requests against a single-limiter bound of 610, i.e. **2.99×** in all three runs. One gateway allowed **609 (1.00×)**.
   - **[E2](../reports/E2-fixed-window-boundary.md)**: the fixed window allowed **200 of 200** within about 235 ms across a boundary (warm runs). A token bucket on the same script allowed **10–12**.
7. **Tests: 118 in total, all passing** (M3 had 109):
   - unit 43 (+6): the fixed window, including the boundary burst at exact instants, plus the same sequence against the token bucket;
   - integration 65 (+3): the algorithm selected by config end to end, and invalid rate-limit settings refusing to start;
   - backend 10.

## Why this order (why *now*)
- **Break it before fixing it.** M5's Redis bucket is a lot of machinery: Lua, atomicity, timeouts, a circuit breaker. Without E1's number, "we need shared state" is an assertion; with it, it's a measured 2.99× defect that M5's P1 must bring back to within ±5% of 610. A fix you can't measure the absence of isn't a fix you can prove.
- **The tooling has to exist before M5 and M6.** The cluster profile, k6 and the reporting habit are reused by every remaining experiment (E3, P1–P4). Building them on simple experiments first means the hard ones only have one unknown.
- **E2 justifies ADR-0002 with data.** The token bucket was chosen in M0 on paper; now there's a measured 200-vs-11 to point to.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [Fixed and sliding windows](../concepts/fixed-and-sliding-windows.md) | §17.11, §17.13 |
| [Load balancing (L4 vs. L7) and shared state](../concepts/load-balancing.md) | §20.2–20.4, §20.10, §15, §16 |
| [Load testing with k6](../concepts/load-testing-with-k6.md) | §23.14–23.15, §27 |
| [Coordinated omission](../concepts/coordinated-omission.md) | §27, §23.14 |

New in the [glossary](../glossary.md): closed vs. open loop, consistent hashing, constant arrival rate, dropped iterations, keep-alive, sliding window counter, sticky sessions, virtual user (VU). The first two experiment reports are in [`docs/reports/`](../reports/).

## Decisions made during M4
| Decision | Why |
|----------|-----|
| A `migrate` one-shot service with the gateway's `--migrate-only` mode, instead of each gateway migrating on startup | Three gateways starting together would race on the same migrations. Spec §17 already plans "migrate once, before rollout" for k3s; this applies the same rule locally. M7 still decides between this mode and an EF migration bundle for the k8s Job. |
| nginx (L7) for the cluster, not something L4 | E1 needs per-request balancing. Behind L4, k6's keep-alive connections could stick to one gateway and hide the bug (spec §21). |
| `RateLimitDecision.Limit` added | The `X-RateLimit-Limit` header must describe the active algorithm: 10 for the bucket, 100 for a 10 s fixed window. Before, the header code assumed a bucket. |
| The algorithm is set from the shell (`FLUX_ALGORITHM`, default `InMemoryTokenBucket`) | Experiments switch algorithms without editing any file, and an unset variable can never select the flawed one. |
| Pinned `grafana/k6:2.3.0` and `nginx:1.30-alpine` | Reproducible reports: a report names the exact versions that produced its numbers. |
| Raw k6 JSON git-ignored; numbers copied into reports | Raw output is large and machine-specific. The report is the durable artefact, with exact commands to regenerate the raw data. |

## How to verify it yourself
Build and test (Docker Desktop running):
```bash
dotnet build -c Release
```
```bash
dotnet test
```
**E1, cluster.** In Git Bash, keep the `MSYS_NO_PATHCONV=1` prefix:
```bash
docker compose -f deploy/compose/docker-compose.yml --profile cluster up --build -d
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet /scripts/e1-instances.js
```
**E1, single-gateway baseline:**
```bash
docker compose -f deploy/compose/docker-compose.yml --profile cluster down
```
```bash
docker compose -f deploy/compose/docker-compose.yml --profile single up -d
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e1-single /scripts/e1-instances.js
```
**E2, the fixed window:**
```bash
FLUX_ALGORITHM=InMemoryFixedWindow docker compose -f deploy/compose/docker-compose.yml --profile single up -d gateway
```
```bash
MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e2-fixed /scripts/e2-boundary.js
```
Run E2 two or three times. The first run on a fresh gateway is a cold start (see the report). Then repeat with `FLUX_ALGORITHM=InMemoryTokenBucket` for the comparison.

**This session's results** (full detail in the reports):

| Experiment | Result |
|------------|--------|
| E1, 3 gateways (3 runs) | allowed 1822 / 1825 / 1825 of 6001 → **2.99×** the 610 bound; each gateway 607–609 |
| E1, 1 gateway (2 runs) | allowed 609 / 609 → **1.00×** |
| E2, fixed window (3 runs) | 115 (cold) / **200** / **200** of 200, burst ≈ 235 ms |
| E2, token bucket (3 runs) | 10 / 11 / 12 of 200 |

## Exercises
1. In E1 each gateway allowed about 608, not 610 and not 600. Where do the extra ~8 over `r × T = 600` come from, and why not the full 10?
2. Change nginx to `hash $http_authorization consistent;` (consistent hashing on the key) and rerun E1. What do you predict? What would break if `gateway-2` died mid-run?
3. Why would E1 likely show **about 1×** if the three gateways sat behind a Kubernetes `Service` (L4) instead of nginx, even though the bug is still there?
4. E2's fixed window used `W = 10 s`. With `W = 60 s` (quota 600), how many requests could the same trick get through in a moment? Does a longer window help?
5. Compute the sliding-window-counter estimate at 10.05 s: previous window 100, current 0, `W = 10 s`. Would it let E2's second batch through?
6. Rewrite E1 as a closed loop: 10 VUs in `constant-vus`, each request followed immediately by the next. If the backend stalled for 2 s, how would that change the *latency percentiles* k6 reports, compared with `constant-arrival-rate`?
7. Why does `InMemoryFixedWindow` use `GetUtcNow()` (wall clock) when `InMemoryTokenBucket` deliberately uses `GetTimestamp()` (monotonic)?

<details><summary>Answers</summary>

1. The bound is `B + r·T`: each bucket starts **full** (10 tokens), then refills at 10/s for 60 s, so the ideal is 610 per bucket. The run allows slightly less (607–609) because:
   - k6's last request lands just before 60 s, so the last fraction of a second of refill is never used;
   - nginx's split isn't *perfectly* even, so a gateway can sit briefly empty while tokens it could have used arrive between its requests.
   The single-gateway run shows the same thing: 609, not 610.
2. Each merchant's key always hashes to the same gateway, so noisy-corp gets **one** bucket and allows about 610 (≈1×), with no Redis at all. If `gateway-2` died, its merchants would be rehashed to the survivors, with **empty** buckets there (a burst of B again, and their history lost). One very noisy merchant would also concentrate all its load on a single instance (a hot partition). And the balancer routes on a *secret* (the key) that hasn't been validated yet.
3. A k8s `Service` balances **connections**. k6 VUs keep their HTTP connections alive, so each connection, and with it all of that VU's requests, sticks to one pod. If the VUs' connections happened to land mostly on one pod, that pod's bucket would do most of the limiting, and the total would look close to 1×. The bug would be hidden by the balancer, not fixed. Spec §21 calls this out, and it's why E1 runs behind nginx locally.
4. **1,200** (600 before the boundary and 600 after), again 2× the per-window quota. A longer window makes the absolute burst *bigger*. The flaw isn't the window length; it's the reset.
5. `estimate = current + previous × (W − elapsed_in_current) / W = 0 + 100 × (10 − 0.05) / 10 = 99.5`. That's already at the quota of 100, so the second batch would be refused, apart from a request or so. That's why the sliding counter is the usual fix when you want "per window" semantics.
6. With 10 closed-loop VUs, a 2 s stall freezes all 10, so only 10 slow samples are recorded, among thousands of fast ones. Even p99 might stay small. The tool also *stops sending* during the stall, so the server sees less load exactly when it's struggling. With `constant-arrival-rate` at 100 req/s, about 200 requests start during the stall and every one of them records the wait. That's [coordinated omission](../concepts/coordinated-omission.md).
7. A fixed window *is* a calendar concept ("the 10 s starting at :00, :10 …"), so it needs real time, and aligning it to the epoch is exactly what makes the boundary predictable (and exploitable). A token bucket only needs *elapsed* time, and it must never gain or lose tokens because NTP nudged the wall clock. So it uses the monotonic clock, which can't jump.
</details>

## What could go wrong
- **`docker compose run k6 … /scripts/x.js` fails with "file not found" in Git Bash.** MSYS rewrites paths that start with `/`. Prefix the command with `MSYS_NO_PATHCONV=1`, or use PowerShell.
- **E1 numbers drift run to run** if gateways aren't restarted. In-memory buckets persist inside the processes. Restart them before each run (`docker compose … restart gateway-1 gateway-2 gateway-3`).
- **The first E2 run after a restart looks "wrong"** (115 instead of 200). It's a cold start, explained in the report. Run it again.
- **`dropped_iterations` > 0** means k6 couldn't keep to the schedule (too few VUs, or the laptop is busy), so the run didn't apply the load it claims. Raise `maxVUs` or close other programs, and rerun.
- **Port 8080 is already in use** when switching profiles. `single` and `cluster` both bind it. `down` one profile before starting the other.
- **A stacked PR merges into the wrong branch.** It happened during this milestone. PR #4 (M3) was opened with base `m2-tenancy-auth` so its diff showed only M3. PR #3 (M2) merged into `main`, and 21 s later PR #4 merged too, but into `m2-tenancy-auth`, an already-merged branch, so **M3 never reached `main`**. Nothing was lost (the commit still existed on `m3-token-bucket`), and it was re-landed with a fresh PR into `main`. The lesson: a stacked PR's base has to be retargeted to `main` *the moment* the PR beneath it merges, or GitHub will happily merge it into a dead branch. From here on, every milestone PR targets `main` directly. The cost is that a PR opened before its predecessor merges briefly shows both milestones' commits.
- **`FLUX_ALGORITHM` lingers** in a shell session. Unset it, or pass it explicitly, so a later experiment doesn't accidentally run on the fixed window.

## Next: M5 — Redis + resilience
- `token_bucket.lua` and `RedisTokenBucket`: one atomic script, Redis's clock, integer return values (ADR-0004).
- A concurrency test: 500 parallel requests against real Redis (Testcontainers) allow **exactly** `B`.
- **E3**: default client timeouts with Redis stopped mid-run. What does a request actually wait?
- `ResilientRateLimiter`: a 50 ms timeout, a circuit breaker, per-route policy (payments fail open with `X-RateLimit-Degraded`, refunds fail closed with `503`).
- **P4**: E3 again with all of that, against spec N4.
