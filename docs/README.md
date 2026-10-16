# Flux documentation

These docs record **what** was built, **why** it was built that way, and **when** each piece was added. They're written to be read alongside your *Software Engineering Field Guide* (referred to as "Field Guide §N").

## Reading order

1. **[spec.md](spec.md)**: the whole system on one page. Start here.
2. **[glossary.md](glossary.md)**: keep it open while reading anything else.
3. **[journal/](journal/)**: the story, one entry per milestone, read in order. Each entry covers:
   - **What** we did and **why**
   - **Why now**: why this step came at this point in the order
   - Concepts introduced, with Field Guide links
   - How to verify it yourself (commands)
   - Exercises, with answers you can expand
   - What could go wrong
4. **[adr/](adr/)**: Architecture Decision Records, one per significant choice, with the options we rejected and why.
5. **[concepts/](concepts/)**: one note per concept, in the Field Guide's template (Problem, Context, Solution, Alternatives, Trade-offs, Implementation, Testing, Observability, Related). They are written as each concept enters the code.
6. **[reports/](reports/)**: the Break/Prove experiment reports, with numbers, machine specs and the exact commands used.

## Index

| Journal | Milestone | Status |
|---------|-----------|--------|
| [00 — Specification](journal/00-specification.md) | M0 | ✅ Done |
| [01 — Skeleton](journal/01-skeleton.md) | M1 | ✅ Done |
| [02 — Tenancy & auth](journal/02-tenancy-auth.md) | M2 | ✅ Done |
| [03 — In-memory token bucket](journal/03-token-bucket.md) | M3 | ✅ Done |
| [04 — Break it](journal/04-break-it.md) | M4 | ✅ Done |
| [05 — Redis + resilience](journal/05-redis-resilience.md) | M5 | ✅ Done |
| [06 — Prove it](journal/06-prove-it.md) | M6 | ✅ Done |
| [07 — Showcase (instead of deploy)](journal/07-showcase.md) | M7 | ✅ Done, in review |

| ADR | Decision |
|-----|----------|
| [0001](adr/0001-stack-dotnet-yarp.md) | .NET 10 + YARP + EF Core |
| [0002](adr/0002-token-bucket.md) | Token bucket as the production algorithm |
| [0003](adr/0003-api-keys-sha256-bearer.md) | Bearer API keys stored as SHA-256 hashes |
| [0004](adr/0004-redis-lua-server-clock.md) | Redis + one atomic Lua script + Redis's clock |
| [0005](adr/0005-per-route-failure-policy.md) | Payments fail open (with local fallback), refunds fail closed |
| [0006](adr/0006-deploy-k3s-actions-tailscale.md) | ~~k3s via GitHub Actions + Tailscale + narrow sudo helper; tailnet only~~ (superseded by 0011) |
| [0007](adr/0007-one-fake-backend-image.md) | One fake-backend codebase run as two services |
| [0008](adr/0008-auth-when-postgres-is-down.md) | PostgreSQL down → cached keys keep working, uncached keys get `503 auth_unavailable` |
| [0009](adr/0009-metrics-system-diagnostics-otel-prometheus.md) | Metrics: `System.Diagnostics.Metrics` + OpenTelemetry's Prometheus exporter |
| [0010](adr/0010-resilience-polly-fail-fast-client.md) | Resilience via Polly v8, a fail-fast Redis client, and "unrequested cancellation = failure" |
| [0011](adr/0011-no-home-server-deploy-publish-images.md) | Don't deploy to the home server; publish images and a recorded demo instead (supersedes 0006) |

| Concept note | Milestone |
|--------------|-----------|
| [Reverse proxy (and YARP)](concepts/reverse-proxy.md) | M1 |
| [The middleware pipeline](concepts/middleware-pipeline.md) | M1 |
| [Containers, Dockerfiles, Compose](concepts/docker-compose.md) | M1 |
| [CI with GitHub Actions](concepts/github-actions-ci.md) | M1 |
| [API-key authentication](concepts/api-key-authentication.md) | M2 |
| [Multi-tenancy](concepts/multi-tenancy.md) | M2 |
| [Caching and negative caching](concepts/caching-and-negative-caching.md) | M2 |
| [EF Core and schema migrations](concepts/ef-core-migrations.md) | M2 |
| [Problem Details and the error contract](concepts/problem-details.md) | M2 |
| [The token bucket](concepts/token-bucket.md) | M3 |
| [Rate-limit headers](concepts/rate-limit-headers.md) | M3 |
| [Testing code that depends on time](concepts/testing-time.md) | M3 |
| [Metrics and cardinality](concepts/metrics-and-cardinality.md) | M3 |
| [Fixed and sliding windows](concepts/fixed-and-sliding-windows.md) | M4 |
| [Load balancing (L4 vs. L7) and shared state](concepts/load-balancing.md) | M4 |
| [Load testing with k6](concepts/load-testing-with-k6.md) | M4 |
| [Coordinated omission](concepts/coordinated-omission.md) | M4 |
| [Redis basics (for a rate limiter)](concepts/redis-basics.md) | M5 |
| [Atomicity with Lua scripts](concepts/lua-atomicity.md) | M5 |
| [Clock skew](concepts/clock-skew.md) | M5 |
| [Timeouts and circuit breakers](concepts/timeouts-and-circuit-breakers.md) | M5 |
| [Fail open vs. fail closed](concepts/fail-open-vs-fail-closed.md) | M5 |
| [Latency percentiles](concepts/latency-percentiles.md) | M6 |
| [Noisy-neighbour isolation](concepts/noisy-neighbour-isolation.md) | M6 |
| [Benchmarking methodology](concepts/benchmarking-methodology.md) | M6 |
| [Containers and registries](concepts/containers-and-registries.md) | M7 |

| Report | Result |
|--------|--------|
| [E1 — per-instance buckets](reports/E1-per-instance-buckets.md) | 3 gateways allowed **2.99×** the limit (1 gateway: 1.00×) |
| [E2 — fixed-window boundary](reports/E2-fixed-window-boundary.md) | **200 of 200** allowed in ~235 ms across a boundary (token bucket: 10–12) |
| [E3 — Redis outage, default timeouts](reports/E3-redis-outage-default-timeouts.md) | requests hang **~5.5 s**, then `500`; afterwards **138 + 30 false `429`s** |
| [P1 — shared Redis bucket](reports/P1-shared-redis-bucket.md) | 3 gateways allowed **608–609 of 610** (E1 was 1,822–1,825); N2 met |
| [P2 — noisy neighbour](reports/P2-noisy-neighbour.md) | quiet merchant's p95 **no increase** under a 100× flood; backend CPU unchanged; N3 met |
| [P3 — gateway overhead](reports/P3-gateway-overhead.md) | **p50 0.30–0.71 ms, p99 1.08–1.57 ms** at 500 req/s with Redis; N1 met |
| [P4 — Redis outage, resilient](reports/P4-redis-outage-resilient.md) | payments **100% served** (degraded, normal latency), refunds `503` in **~1.1 ms**, recovery **~4.5 s**; N4 met (one caveat) |
