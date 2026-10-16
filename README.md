# Flux

[![CI](https://github.com/Kar6677thik/Flux/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Kar6677thik/Flux/actions/workflows/ci.yml)

A multi-tenant API gateway and rate limiter in ASP.NET Core 10 and YARP. Each merchant is authenticated by API key and held to its plan's limit **per route**, through one atomic Redis Lua script shared by every gateway instance. When Redis fails, payments degrade gracefully and refunds fail closed. Inspired by Razorpay's Edge gateway. It's a learning project: Project 1 of *10 Engineering Projects from Real Company Problems*, documented end to end in [`docs/`](docs/README.md).

> **Status:** complete (M0–M7). Images are published to GHCR on every merge; Flux is deliberately not hosted anywhere ([ADR-0011](docs/adr/0011-no-home-server-deploy-publish-images.md)). Run it in two commands (below) or watch it work:

![A 30-second terminal demo: an authenticated request with rate-limit headers; noisy-corp's burst of 25 refunds gets 2 allowed and 23 × 429; Redis is stopped, payments keep flowing with X-RateLimit-Degraded while refunds get 503; Redis returns and everything is normal again.](docs/images/demo.svg)

<sub>Recorded from [`bench/demo.sh`](bench/demo.sh) against a real local stack. Every status code, header and number is a real response.</sub>

## Architecture
```mermaid
flowchart LR
    C["Client / k6"] -->|"Authorization: Bearer flux_..."| LB["nginx (local, L7)<br/>k8s Service (server)"]
    LB --> G1["Gateway #1"]
    LB --> G2["Gateway #2"]
    LB --> G3["Gateway #3"]
    G1 & G2 & G3 -->|"key + plan lookup<br/>(cached 60 s / 10 s)"| PG[("PostgreSQL<br/>merchants, plans, keys")]
    G1 & G2 & G3 -->|"EVALSHA token_bucket.lua<br/>50 ms timeout + circuit breaker"| R[("Redis<br/>shared buckets")]
    G1 & G2 & G3 -->|"YARP forward<br/>X-Flux-Merchant-Id"| P["payments"]
    G1 & G2 & G3 -->|"YARP forward"| F["refunds"]
```
Each request: correlation id → strip client `X-Flux-*` headers → route group → API-key auth (cached) → rate limit (Redis, or the per-route failure policy) → forward → `X-RateLimit-*` + `Server-Timing` on the way back. Details: [spec §5](docs/spec.md).

## Results (measured)
All numbers were measured on one laptop (Intel Core Ultra 5 225H, Docker Desktop on WSL2, k6 capped at 2 CPUs). Each links to its report, with exact commands and every run.

| Claim | Measured | Target | Report |
|-------|----------|--------|--------|
| In-memory buckets across 3 instances let a merchant through at ~3× its limit | **2.99×** (1,822–1,825 allowed vs. a bound of 610), 3 runs | (break) | [E1](docs/reports/E1-per-instance-buckets.md) |
| A fixed window allows a boundary burst | **200 of 200** in ~235 ms on a 10 req/s plan (token bucket: 10–12) | (break) | [E2](docs/reports/E2-fixed-window-boundary.md) |
| Client defaults turn a Redis outage into an outage of everything | requests hang **~5.5 s**, then `500`; afterwards **138 + 30 false `429`s** from a replayed queue | (break) | [E3](docs/reports/E3-redis-outage-default-timeouts.md) |
| **One shared Redis bucket enforces the limit across 3 gateways** | **608–609 of 610** (≤ 0.3% error), 3 runs | ±5% (N2) | [P1](docs/reports/P1-shared-redis-bucket.md) |
| **A merchant flooding at 100× its limit doesn't slow a quiet one** | quiet merchant's p95 **no increase** (3 paired runs); flooder got exactly its 609; backend CPU unchanged at 2% | p95 ≤ +10% (N3) | [P2](docs/reports/P2-noisy-neighbour.md) |
| **The gateway adds little latency** | **p50 0.30–0.71 ms, p99 1.08–1.57 ms** at 500 req/s with Redis, by two methods; the Redis check is ~86% of it | p50 ≤ 2 ms, p99 ≤ 10 ms (N1) | [P3](docs/reports/P3-gateway-overhead.md) |
| **A Redis outage is survivable** | payments **100% served** (degraded) at normal latency; refunds `503` in **~1.1 ms** (p50); automatic recovery in **4.1–4.6 s** | N4 | [P4](docs/reports/P4-redis-outage-resilient.md) |

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/limits-dark.svg">
  <img alt="Bar chart: requests allowed in 60 s for noisy-corp offering 100 req/s on a 10 req/s plan. 3 gateways with in-memory buckets: 1,822–1,825. 3 gateways with a shared Redis bucket: 608–609. 1 gateway: 609. The plan limit is 610." src="docs/images/limits-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/outage-dark.svg">
  <img alt="Requests per second by outcome during a 21-second Redis outage. With client defaults (E3), payments and refunds fail with 500s, then a burst of 429s follows recovery. With the resilient limiter (P4), all payments succeed in degraded mode and refunds get 503 until Redis returns." src="docs/images/outage-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/outage-latency-dark.svg">
  <img alt="Payments p99 latency per second, log scale. With client defaults, p99 jumps to about 6 seconds for the whole outage. With the resilient limiter it stays between 10 and 20 ms." src="docs/images/outage-latency-light.svg">
</picture>

<sub>Charts drawn by [`bench/make_charts.py`](bench/make_charts.py) from the experiments' raw k6 output; the table above is the same data in text form.</sub>

Caveat, from P4: during a *silent* outage (Redis frozen, connections still open), the slowest requests in the ~2 s before the circuit breaker opened took 60–75 ms over normal, not the targeted ≤ 50 ms.

## Run it
**Just Docker, nothing to build.** Uses the images CI publishes to GHCR:
```bash
git clone https://github.com/Kar6677thik/Flux.git && cd Flux
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.ghcr.yml --profile single up -d
curl -i localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"
```
The first start pulls images and migrates the database. Give it ~20 s, or watch `curl localhost:8080/readyz`. To replay the demo, start with the Redis limiter instead and run the script (it stops and restarts the Compose Redis):
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.ghcr.yml --profile single up -d
bench/demo.sh
```

**From source** (needs the .NET SDK 10.0.401+):
```bash
dotnet build -c Release && dotnet test                       # 135 tests; integration tests use real PostgreSQL and Redis
docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
curl -i localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"
```
The dev keys and admin token are public on purpose and exist only in Development; the gateway refuses to seed them anywhere else. Three gateways behind nginx: `--profile cluster`. The experiments are one command each: see [`bench/README.md`](bench/README.md).

## What's inside
- **Auth:** `flux_` + 43 base62 characters (32 random bytes). Stored as SHA-256 plus a 12-character prefix, shown once. Positive cache 60 s, negative cache 10 s ([ADR-0003](docs/adr/0003-api-keys-sha256-bearer.md)).
- **Rate limiting:**
  - token bucket per `(merchant, route group)`, lazy refill, headers in delta seconds ([ADR-0002](docs/adr/0002-token-bucket.md));
  - one Lua script on Redis's clock ([ADR-0004](docs/adr/0004-redis-lua-server-clock.md));
  - three algorithms behind one interface (Strategy), chosen by config.
- **Resilience:**
  - Polly circuit breaker around a 50 ms timeout, with a fail-fast client ([ADR-0010](docs/adr/0010-resilience-polly-fail-fast-client.md));
  - payments fail open to `limit ÷ instances`, refunds fail closed with a `503` ([ADR-0005](docs/adr/0005-per-route-failure-policy.md)).
- **Observability:** `/metrics` (Prometheus, no per-merchant labels), `Server-Timing`, JSON logs carrying `requestId`/`merchantId`/`keyPrefix` and never full keys ([ADR-0009](docs/adr/0009-metrics-system-diagnostics-otel-prometheus.md)).
- **Tests:** xUnit v3, Testcontainers (real PostgreSQL 17 and Redis 7.4, never mocked), `FakeTimeProvider` instead of sleeping.

## Résumé line
> Built a multi-tenant API gateway and rate limiter in ASP.NET Core/YARP with Redis-backed token buckets (atomic Lua). Measured: cut cross-instance over-admission from 2.99× to within 0.3% of quota across 3 instances, added 0.3–0.7 ms p50 / 1.1–1.6 ms p99 overhead at 500 req/s, and kept 100% of payments flowing through a Redis outage while refunds failed closed in ~1 ms.

Every number in that line comes from a report above. Say "Flux (API gateway)" to avoid confusion with Flux CD.

## Milestones
M0 Spec ✅ · M1 Skeleton ✅ · M2 Tenancy & auth ✅ · M3 Token bucket ✅ · M4 Break it ✅ · M5 Redis + resilience ✅ · M6 Prove it ✅ · M7 Showcase ✅ *(instead of a deploy: [ADR-0011](docs/adr/0011-no-home-server-deploy-publish-images.md))*

The story of each, with exercises, is in [`docs/journal/`](docs/journal/); start at [`docs/README.md`](docs/README.md).
