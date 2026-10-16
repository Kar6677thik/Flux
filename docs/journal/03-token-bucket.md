# Journal 03 — In-memory token bucket (M3)

- **Date:** 2026-10-15
- **Milestone:** M3
- **Branch:** `m3-token-bucket` (stacked on `m2-tenancy-auth`, PR #3)

## What we did
1. **The limiter interface** ([`IRateLimiter`](../../src/Flux.Gateway/RateLimiting/IRateLimiter.cs)), exactly as spec §9.1 describes it: `TryAcquireAsync(key, limit)` → `RateLimitDecision(Allowed, Remaining, RetryAfter, ResetAfter, Source)`. Config picks the implementation (`RateLimiting:Algorithm`), and an unknown name fails at startup.
2. **[`InMemoryTokenBucket`](../../src/Flux.Gateway/RateLimiting/InMemoryTokenBucket.cs)**:
   - lazy refill on a monotonic clock, one lock per bucket;
   - floating-point rounding that never under-promises;
   - idle buckets evicted after 10 minutes.
3. **[`RateLimitMiddleware`](../../src/Flux.Gateway/RateLimiting/RateLimitMiddleware.cs)**, step 5 of the request lifecycle:
   - one bucket per `(merchant, route group)`, with limits from M2's `PlanCatalog`;
   - limited requests get `429 rate_limit_exceeded` and are **never forwarded**.
4. **The header contract** ([`RateLimitHeaders`](../../src/Flux.Gateway/RateLimiting/RateLimitHeaders.cs)):
   - `X-RateLimit-Limit/Remaining/Reset` on every rate-limited response, plus `Retry-After` on `429`;
   - delta seconds, always rounded up.
5. **[`RouteGroupMiddleware`](../../src/Flux.Gateway/Proxy/RouteGroupMiddleware.cs)**: spec step 3, made explicit as the first step in YARP's pipeline. A route without valid `RouteGroup` metadata is a loud error, not a shared default bucket.
6. **Metrics** ([`FluxMetrics`](../../src/Flux.Gateway/Observability/FluxMetrics.cs), [ADR-0009](../adr/0009-metrics-system-diagnostics-otel-prometheus.md)):
   - `/metrics` exposes `flux_requests_total{route_group,outcome}`, `flux_gateway_overhead_seconds` and `flux_ratelimiter_duration_seconds{algorithm}`;
   - histogram buckets are sized for seconds;
   - no merchant label anywhere.
7. **`Server-Timing`** ([`RequestTelemetryMiddleware`](../../src/Flux.Gateway/Observability/RequestTelemetry.cs) + [`UpstreamTimingMiddleware`](../../src/Flux.Gateway/Proxy/UpstreamTimingMiddleware.cs)): `gw;dur=…, upstream;dur=…` on every response, so P3 can measure overhead per request.
8. **Tests: 109 in total, all passing, three full runs in a row** (M2 had 76):
   - unit 37 (+20): bucket maths at exact instants, header rounding;
   - integration 62 (+13): the full contract through the real gateway, metrics scraped from `/metrics`;
   - backend 10.

## Why this order (why *now*)
- **The limiter needs M2's output.** The bucket key is `(merchantId, routeGroup)`, and the limits come from the plan. Both now exist, and nothing has to be faked.
- **In memory before Redis.** The *maths* (refill, rounding, headers) and the *distribution* (shared state, atomicity, failure) are separate problems. Solving the maths first, with exact unit tests, means that in M5 any difference between the two implementations points to the distribution code. This in-memory bucket also *becomes* M5's fallback limiter.
- **Metrics now, before the experiments.** M4's experiments need `flux_requests_total{outcome}` to count allowed versus limited, and P3 needs `Server-Timing`. Adding measurement after the experiments would mean rerunning them.
- **It also sets up the next lesson.** This limiter is correct for one instance. M4 deliberately runs three and shows it allowing about 3× the limit.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [The token bucket](../concepts/token-bucket.md) | §17.11, §17.13–14, §20.16, §3 (Strategy), §14 |
| [Rate-limit headers](../concepts/rate-limit-headers.md) | §7.16, §17.11 |
| [Testing code that depends on time](../concepts/testing-time.md) | §23.1, §23.27, §23.30–31 |
| [Metrics and cardinality](../concepts/metrics-and-cardinality.md) | §24, §27 |

Updated: [the middleware pipeline](../concepts/middleware-pipeline.md), now showing all eight steps in order. New in the [glossary](../glossary.md): delta seconds, histogram, MeterListener, monotonic clock, OpenTelemetry, Prometheus, RED method, `Server-Timing`, Strategy pattern, time series.

## Decisions made during M3
| Decision | Why |
|----------|-----|
| [ADR-0009](../adr/0009-metrics-system-diagnostics-otel-prometheus.md): `System.Diagnostics.Metrics` + OpenTelemetry's Prometheus exporter (beta, pinned) | Closes spec §22. The instruments use .NET's built-in API, so the beta exporter is one replaceable line. |
| Rate-limit headers are applied in `Response.OnStarting` | YARP copies the backend's headers when forwarding, so headers set earlier could be overwritten. `OnStarting` runs after that copy. |
| Durations are rounded **up** to the next 100 ns tick | Found by an exact-instant test: `TimeSpan.FromSeconds` truncated 0.07 s to 0.0699999 s, so a client that waited exactly `RetryAfter` would be refused again. |
| Tokens rounded to 1e-9 after each refill | So floating-point drift can't leave 2.9999999 tokens where the maths says 3. |
| No plan limit known → `503 rate_limiter_unavailable`, `Retry-After: 5` | Only possible before the catalog has loaded, when the gateway is unready anyway. Refusing beats guessing a limit. |
| Outcome `auth_unavailable` added to the metric | ADR-0008's `503` needed a label. The outcome list stays closed (constants), so cardinality stays bounded. |
| `none` as the route group for `not_found` | A 404 has no group, and a fixed placeholder keeps the series count bounded. |
| Metrics-asserting tests run in a non-parallel collection | A `MeterListener` is process-wide: parallel gateways in one test process see each other's meters. |

## How to verify it yourself
Build and test (Docker Desktop running):
```bash
dotnet build -c Release
```
```bash
dotnet test
```
Start the stack:
```bash
docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
```
One request from `noisy-corp` (free: 10/s on payments). Look at `X-RateLimit-*` and `Server-Timing`:
```bash
curl -i localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM"
```
A real burst of 25 *parallel* requests (Git Bash):
```bash
seq 1 25 | xargs -P 25 -I{} curl -s -o /dev/null -w "%{http_code}\n" localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM" | sort | uniq -c
```
Exhaust the refunds bucket (free: 2/s), then look at a `429`:
```bash
seq 1 15 | xargs -P 15 -I{} curl -s -o /dev/null localhost:8080/v1/refunds/rfnd_1 -H "Authorization: Bearer flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM"; curl -i localhost:8080/v1/refunds/rfnd_1 -H "Authorization: Bearer flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM"
```
The metrics:
```bash
curl -s localhost:8080/metrics | grep ^flux_
```
These are the numbers measured in this session (Windows laptop, Docker Desktop):

| What | Result |
|------|--------|
| 25 **sequential** `curl`s from noisy-corp | 22 × `200`, 3 × `429`: not a burst at all (see below) |
| 25 **parallel** `curl`s from noisy-corp | 14 × `200`, 11 × `429` |
| 25 parallel from quiet-shop (pro: burst 100) | 25 × `200` |
| `429` on refunds | `Retry-After: 1`, `X-RateLimit-Limit: 2`, `Remaining: 0`, `Reset: 1`, `detail: "Merchant plan 'free' allows 2 requests/s on route group 'refunds'."` |
| `Server-Timing` on a forwarded request | `gw;dur=0.084, upstream;dur=6.016` (ms) |
| `/metrics` after the sequential requests (39 in all) | `allowed` 36, `limited` 3, overhead sum 0.084 s |

**Why not exactly 10 × `200`?** Because neither run was an *instant* burst:
- **Sequential:** each `curl` starts a process and waits about 6 ms on the backend (`Backend__LatencyMs=5`). At 10 tokens/s, a token comes back every 100 ms, so the bucket nearly keeps up.
- **Parallel:** on Windows, `xargs` still needs a few hundred milliseconds to *start* 25 processes, so about 4 tokens refill during the "burst".

The exact `10 + 15` is proven where time is controlled: `The_eleventh_request_in_an_instant_is_429_with_retry_after` (integration) and `A_new_bucket_starts_full_and_allows_exactly_its_burst` (unit). Measuring with real arrival times is what k6 is for in M4. This is also a first taste of *coordinated omission*: a load generator that waits for each answer sends less load than you think.

## Exercises
1. Free plan (B = 10, r = 10/s). A client sends one request every 50 ms for 10 s. How many are allowed? Check your answer against `The_average_rate_is_enforced_over_a_long_run`.
2. `RetryAfter` is 70 ms, but the header says `1`. Why round up instead of to the nearest second? What would a client do with `Retry-After: 0`?
3. Remove `lock (_gate)` in `Bucket.TryTake` and run `Concurrent_requests_never_spend_the_same_token_twice` a few times. What do you see, and why can the result vary between runs?
4. Why does `RateLimitMiddleware` set its headers in `Response.OnStarting` instead of directly on `http.Response.Headers` before calling `next`?
5. Someone proposes adding a `merchant_id` label to `flux_requests_total` "so we can see who's noisy". With 50,000 merchants, how many series does that metric become? Where should the question be answered instead?
6. Three gateway instances run behind a round-robin load balancer, each with this `InMemoryTokenBucket`. `noisy-corp` (free) sends 100 req/s for 60 s. Roughly how many requests are allowed? (That's M4's experiment E1. Write your prediction down before you run it.)
7. Why is `Server-Timing`'s `upstream` measured until the response *starts*, and what would change if it were measured until the body finished?

<details><summary>Answers</summary>

1. **109.** Requests arrive at t = 0, 0.05 … 9.95 s. The bucket starts with 10 tokens and gains 0.5 per step, so the total available is `10 + 10/s × 9.95 s = 109.5` tokens, which allows 109 whole requests. That is the `B + r·T` bound minus the part of the last interval nobody used.
2. A client that waits the advertised time must succeed. Rounding 70 ms to the nearest second gives `0`, and `0` means "retry immediately", which fails again, a retry storm in miniature. Rounding *up* (and a minimum of `1`) can only make a client wait slightly too long, never too short.
3. Sometimes more than 100 are allowed. Two threads both read `_tokens = 1`, both see `≥ 1`, and both subtract: a *read-modify-write race*. It depends on thread timing, so some runs pass. That's why the test fires 1,000 requests: to make the window likely. A passing concurrency test is evidence, not proof. It's also exactly the race M5's Lua script prevents across processes.
4. For an allowed request, the response that goes out is the backend's, and YARP copies the backend's response headers onto it while forwarding. Anything set *before* `next` could be overwritten, and if the backend sent its own `X-RateLimit-*`, the client would see the backend's values. `OnStarting` runs at the last moment before the first byte, after YARP's copy. It also covers the gateway's own `429`, which goes through the same callback.
5. 50,000 merchants × (up to) 21 route-group/outcome combinations = **over a million series** for one metric. Prometheus memory and query time grow with that. Answer "who's noisy?" from **logs**, which carry `merchantId` on every line after auth (e.g. count `429`s by `merchantId` in Loki), or from per-merchant usage tables. Metrics answer "how much, overall"; logs answer "who".
6. About **3×** the quota: `3 × (10 + 10 × 60) ≈ 1,830`, instead of the single-instance bound of `10 + 600 = 610`. Each instance has its own full bucket and refills independently. Round-robin spreads the merchant's traffic over all three, so each instance sees only about 33 req/s, and each one still allows its own 10/s. M4 measures the real multiple.
7. "Until it starts" is the backend's *time to first byte*: how long we waited before it answered. That is the part the gateway can't speed up. If we measured until the body finished, `upstream` would also include streaming the body to a slow client, and `gw` would come out wrong (possibly negative). Also, the header has to be written *before* the body starts, so we couldn't know the end time when writing it anyway.
</details>

## What could go wrong
- **Expecting exactly 10 allowed from a `curl` burst.** See above: process start-up and backend latency spread the requests out. Use k6 (M4) for real arrival-rate tests.
- **Metrics tests failing only in the full run.** Parallel test gateways in one process share a `MeterListener` scope. Any new test that asserts *counts* belongs in `MetricsCollection`.
- **The beta exporter changes its output format** (for example the `otel_scope_name` label) after a version bump. `ObservabilityTests` asserts the exact lines, so the upgrade PR fails visibly. Check the release notes before you bump.
- **A new route without `Metadata.RouteGroup`** → every request to it is a `500`. That's deliberate (loud beats a silently shared bucket). Add the metadata in `appsettings.json`.
- **A plan with `B / r` over 10 minutes** (e.g. a daily quota) would be wrongly reset by idle eviction. That isn't possible with today's plans; revisit eviction if one is added.

## Next: M4 — Break it
- `InMemoryFixedWindow`, the deliberately flawed algorithm.
- A Compose `cluster` profile: 3 gateways behind nginx (L7 round-robin).
- k6 scripts, run from Docker.
- **E1**: 3 instances × in-memory buckets → about 3× the limit.
- **E2**: the fixed window's boundary burst.
- Both written up in `docs/reports/` with measured numbers.
