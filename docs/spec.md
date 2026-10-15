# Flux — Specification (v1.0)

> **Status:** Agreed specification, written 2026-10-15 before any code exists.
> **Scope:** Project 1 of *10 Engineering Projects from Real Company Problems*: a multi-tenant API gateway and rate limiter, inspired by Razorpay's Edge gateway.
> **How to read this:** Sections 1–3 explain *why*. Sections 4–12 describe *what* we build, precisely enough to write tests against. Sections 13–17 cover *how we prove it works* and *where it runs*. Sections 18–22 cover the *order of work* and what is still open.
> Any decision with real alternatives has an ADR in [`adr/`](adr/). New words are defined in [`glossary.md`](glossary.md).

---

## 1. The problem

Razorpay routes all merchant API traffic through **Edge**, its own API gateway. Edge has to absorb abusive traffic and DDoS. Its rate-limiter plugin handles more than 200K requests/second, and authorization checks take 5–8 ms. Interview guides list the *multi-tenant rate limiter* as Razorpay's signature design question. Redis's April 2026 post on throttling lists two mistakes teams still make:

1. **Fixed windows** allow bursts at the window boundary (up to 2× the quota in a moment).
2. **One global limit** is shared by every client, so one noisy client starves the rest.

Flux rebuilds this problem at learning scale. A gateway sits in front of two backend services ("payments" and "refunds"). It authenticates each merchant by API key and limits each merchant's traffic per route, according to their plan. It also behaves correctly when several gateway instances run at once and when Redis fails.

## 2. Goals

| # | Goal | How we will know |
|---|------|------------------|
| G1 | Authenticate every request by API key and map it to a merchant (tenant). | Integration tests; unknown and revoked keys get `401`. |
| G2 | Enforce per-merchant, per-route limits taken from the merchant's plan. | Unit + integration tests; experiment P1. |
| G3 | Return a predictable header contract (`429`, `Retry-After`, `X-RateLimit-*`). | Contract tests (§7.4). |
| G4 | Keep limits correct across 3 gateway instances. | Experiment E1 (broken) → P1 (fixed). |
| G5 | Behave deliberately when Redis is down, chosen per route. | Experiment E3 → P4; ADR-0005. |
| G6 | Protect quiet merchants from noisy ones. | Experiment P2 (quiet merchant's p95 stays flat). |
| G7 | Measure the latency the gateway itself adds. | Experiment P3 (p50/p99). |
| G8 | Deploy to the home server automatically from GitHub Actions. | M7: a push to `main` results in a running, smoke-tested deployment. |
| G9 | Document everything so the author can learn the whole system end to end. | `docs/` journal, concept notes, ADRs and reports for every milestone. |

## 3. Non-goals

These are out of scope so the project stays finishable. Each could be a later extension.

- Real payment logic. The backends are deliberately fake.
- OAuth, JWT, merchant dashboards or a self-service UI.
- TLS inside the gateway. Tailscale Serve terminates HTTPS on the server.
- Per-merchant limit overrides. Limits come from the plan only (stretch goal).
- Rate limiting unauthenticated traffic by IP address (stretch goal).
- A highly available Redis (Sentinel or Cluster). One Redis instance; its failure is an experiment, not something we prevent.
- A Prometheus/Grafana stack. Flux exposes `/metrics`; dashboards arrive in Project 5.
- Kubernetes autoscaling (HPA), multiple regions, billing or usage metering.

## 4. System overview

```mermaid
flowchart LR
    C["Client / k6"] -->|"Authorization: Bearer flux_..."| LB["nginx (local)<br/>k8s Service (server)"]
    LB --> G1["Gateway #1"]
    LB --> G2["Gateway #2"]
    LB --> G3["Gateway #3"]
    G1 & G2 & G3 -->|"key + plan lookup<br/>(cached in memory)"| PG[("PostgreSQL<br/>merchants, plans, keys")]
    G1 & G2 & G3 -->|"EVALSHA token_bucket.lua"| R[("Redis<br/>buckets")]
    G1 & G2 & G3 -->|"YARP forward"| P["payments service"]
    G1 & G2 & G3 -->|"YARP forward"| F["refunds service"]
```

| Component | Technology | Responsibility | Owns data? |
|-----------|-----------|----------------|-----------|
| `Flux.Gateway` | ASP.NET Core 10 + YARP | Correlation IDs, API-key auth, rate limiting, header contract, proxying, admin API, metrics | Yes: merchants, plans and keys in PostgreSQL |
| `Flux.Backend` (run twice, as `payments` and `refunds`) | ASP.NET Core 10 minimal API | Fake business endpoints with configurable latency | No (stateless) |
| PostgreSQL 17 | — | Source of truth for merchants, plans and API keys | — |
| Redis 7 | — | Shared token buckets (from milestone M5). **Not** a source of truth: losing it only resets limits. | — |
| nginx | — | Local only: round-robin load balancer in front of 3 gateways | — |
| k6 | — | Load generation for experiments | — |

Why one backend image for two services: they are identical fakes, so one codebase avoids duplication. At runtime they are still two separate services with separate names and addresses (see [ADR-0007](adr/0007-one-fake-backend-image.md)).

## 5. Request lifecycle

What happens to one request, in order. Each step is an ASP.NET Core middleware or a YARP stage, in exactly this order.

1. **Correlation.** If `X-Request-Id` is present and valid (1–64 characters from `[A-Za-z0-9_-]`), keep it; otherwise generate a new one. It goes into log scopes, onto the upstream request and onto the response.
2. **Header sanitising.** Remove every inbound header starting with `X-Flux-`. Only the gateway may set them, and backends trust them. If we skipped this step, a client could forge `X-Flux-Merchant-Id` and act as another merchant.
3. **Route match.** Map the path to a *route group* (`payments` or `refunds`) using the table in §7.1. Gateway-owned paths (`/healthz`, `/readyz`, `/metrics`, `/admin/*`) skip steps 4–6. Unknown paths → `404`.
4. **Authentication** (§8):
   - A missing or malformed `Authorization` header → `401 missing_api_key`.
   - An unknown or revoked key → `401 invalid_api_key`.
   - A suspended merchant → `403 merchant_suspended`.
5. **Rate limiting** (§9). The bucket key is `(merchantId, routeGroup)`, with limits from the merchant's plan. There are three possible outcomes:
   - *allowed* → continue to step 6;
   - *limited* → `429` with headers; the request is **not** forwarded;
   - *limiter unavailable* → follow the route's failure policy (§9.5).
6. **Forward.** Add `X-Flux-Merchant-Id`, `X-Flux-Plan` and `X-Request-Id`. YARP forwards the request with the `/v1` prefix removed. The upstream timeout is 10 s.
7. **Respond.** Add rate-limit headers (§7.3), `X-Request-Id` and `Server-Timing` (§11.2) to whatever the backend returned. If the backend is unreachable → `502 upstream_unavailable`. If it times out → `504 upstream_timeout`.

**Rules that follow from this order:**
- A rejected request (`401`/`403`/`429`) never reaches a backend.
- A token is spent when the request is *admitted*, even if the backend later fails. This is the standard behaviour: we are protecting the backend, and it did receive the request.
- Limits are per **merchant**, not per API key. If they were per key, a merchant could create ten keys and get ten times their limit.

## 6. Tenancy model

- **Merchant** = tenant. Each merchant has exactly one plan and an `active` or `suspended` status.
- **Plan** = a named set of limits, one per route group.
- **API key** = a secret that identifies one merchant. A merchant can have several keys (to support key rotation). Keys can be revoked.

Initial plans (seeded by a migration):

| Plan | Route group | Refill rate (tokens/s) | Burst capacity (tokens) |
|------|-------------|------------------------|------------------------|
| free | payments | 10 | 10 |
| free | refunds | 2 | 2 |
| pro | payments | 100 | 100 |
| pro | refunds | 20 | 20 |

Burst capacity equals the per-second rate at the start, so "10 requests/s" means what it says. Capacity is stored separately, so a later plan can allow bigger bursts without code changes.

## 7. Public API contract

### 7.1 Routes

| Public route | Route group | Upstream service | Upstream path |
|--------------|-------------|------------------|---------------|
| `POST /v1/payments` | payments | payments | `POST /payments` |
| `GET /v1/payments/{id}` | payments | payments | `GET /payments/{id}` |
| `POST /v1/refunds` | refunds | refunds | `POST /refunds` |
| `GET /v1/refunds/{id}` | refunds | refunds | `GET /refunds/{id}` |

GET and POST share one bucket per route group.

**Fake backend behaviour:**
- Requests without `X-Flux-Merchant-Id` are rejected with `400`. This proves the backends only accept traffic coming through the gateway.
- `POST /payments` accepts `{ "amount": 50000, "currency": "INR" }` (amount in paise, > 0) and returns `201` with `{ id: "pay_<random>", merchantId, amount, currency, status: "created" }`.
- `POST /refunds` accepts `{ "paymentId": "pay_…", "amount": 1000 }` and returns `201` with `{ id: "rfnd_<random>", … }`.
- `GET` endpoints return a plausible fake object for any id. The backends are stateless on purpose.
- `Backend__LatencyMs` (default 5) adds an artificial delay so the backends behave a bit more like real services.

### 7.2 Authentication

```
Authorization: Bearer flux_3kX9vQ2mN8pL4rT7wY1zA6bC5dE0fG2hJ
```

- Format: `flux_` followed by 32 random bytes in base62 (≈43 characters).
- The first 12 characters (`flux_3kX9vQ2`) are the **key prefix**. It is safe to show and log, and it is how humans tell keys apart.
- The full key is shown **once**, when it is created. After that only its SHA-256 hash is stored. See [ADR-0003](adr/0003-api-keys-sha256-bearer.md) for why SHA-256 and not bcrypt.

### 7.3 Rate-limit headers

These are sent on **every** response from a rate-limited route, whether the request was allowed or limited:

| Header | Meaning | Example |
|--------|---------|---------|
| `X-RateLimit-Limit` | Bucket capacity: the most requests you could send in one instant | `10` |
| `X-RateLimit-Remaining` | Whole tokens left after this request | `7` |
| `X-RateLimit-Reset` | Seconds (rounded up) until the bucket is completely full again | `1` |
| `Retry-After` | **Only on `429` and `503`.** Seconds (rounded up, minimum 1) until at least one token exists. | `1` |
| `X-RateLimit-Degraded` | Only present, as `true`, when the local fallback limiter made the decision (§9.5) | `true` |

`Reset` and `Retry-After` are *delta seconds*, not Unix timestamps. A timestamp would depend on the client's clock matching ours, and delta seconds avoid that clock-skew problem. The IETF is standardising `RateLimit` / `RateLimit-Policy` headers (draft-ietf-httpapi-ratelimit-headers). We use the widely recognised `X-RateLimit-*` names and discuss the draft in the M3 concept note.

### 7.4 Error contract

Every error uses **RFC 9457 Problem Details** (`Content-Type: application/problem+json`), with a stable machine-readable `code` and the request ID:

```json
{
  "type": "https://flux.local/errors/rate_limit_exceeded",
  "title": "Rate limit exceeded",
  "status": 429,
  "detail": "Merchant plan 'free' allows 10 requests/s on route group 'payments'.",
  "code": "rate_limit_exceeded",
  "requestId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

| Status | `code` | When |
|--------|--------|------|
| 400 | `invalid_request` | Validation failure (backend or admin API) |
| 401 | `missing_api_key` | No `Authorization: Bearer` header, or it is malformed |
| 401 | `invalid_api_key` | Key unknown or revoked |
| 403 | `merchant_suspended` | Merchant status is `suspended` |
| 404 | `route_not_found` | No route matches |
| 429 | `rate_limit_exceeded` | Bucket empty |
| 502 | `upstream_unavailable` | Backend refused the connection or is down |
| 503 | `rate_limiter_unavailable` | Redis unavailable on a fail-closed route (§9.5) |
| 504 | `upstream_timeout` | Backend took longer than 10 s |

*Added in M2* (these situations weren't covered above):

| Status | `code` | When |
|--------|--------|------|
| 401 | `invalid_admin_token` | Admin endpoint called without a valid `X-Admin-Token` |
| 404 | `merchant_not_found` / `key_not_found` | Admin API: no merchant or key with that id |
| 500 | `internal_error` | An unhandled bug in the gateway (never includes a stack trace) |
| 503 | `auth_unavailable` | PostgreSQL unreachable and the key isn't cached. `Retry-After: 5`. See [ADR-0008](adr/0008-auth-when-postgres-is-down.md) |

Error responses never contain stack traces, API keys or internal hostnames.

## 8. Authentication design

**Lookup path for each request:**

```
key → SHA-256 → in-memory cache (TTL 60 s) → hit?  → (merchant, plan, status)
                                           → miss? → PostgreSQL → cache result
```

- **Positive cache, TTL 60 s.** The database is not touched on the hot path. Trade-off: revoking a key, suspending a merchant or changing a plan takes up to 60 s to reach every gateway instance. We accept and document that delay.
- **Negative cache, TTL 10 s.** Unknown keys are cached too. Otherwise a flood of random keys would turn into a flood of database queries (*cache penetration*, Field Guide §12.12).
  - *Clarified in M2:* this protects against the **same** bad key repeated, not against many *different* random keys, each of which is a first-time miss. The negative cache is capped (`ApiKeys__MaxCacheEntries`) so that such a flood can't exhaust memory either. See the [caching note](concepts/caching-and-negative-caching.md).
- **Plans and limits** are loaded into memory at startup and refreshed every 60 s.
- **Comparison safety.** We look keys up by hash through a unique index, so there is no character-by-character comparison of secrets. The admin token *is* compared directly, so it uses `CryptographicOperations.FixedTimeEquals`.
- **Logging.** Only the key prefix is ever logged.

## 9. Rate-limiting design

### 9.1 One interface, several algorithms

```csharp
public interface IRateLimiter
{
    ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct);
}
// RateLimitDecision: Allowed (bool), Remaining, RetryAfter, ResetAfter, Source (Primary | Fallback)
```

Configuration picks the implementation (`RateLimiting__Algorithm`). This is the Strategy pattern, and it earns its place here: the Break/Prove experiments need to swap algorithms without changing any calling code.

| Implementation | Milestone | Purpose |
|----------------|-----------|---------|
| `InMemoryTokenBucket` | M3 | The first working limiter. Each instance keeps its own buckets. |
| `InMemoryFixedWindow` | M4 | Deliberately flawed. Used to demonstrate the boundary burst (E2). |
| `RedisTokenBucket` | M5 | Production design: shared buckets, one atomic Lua script. |
| `ResilientRateLimiter` (decorator) | M5 | Wraps Redis with a timeout, a circuit breaker and the per-route failure policy. |

### 9.2 Token bucket semantics

A token bucket has capacity `B` and refill rate `r` tokens/second. It starts full. Each request costs 1 token.

```
tokens  = min(B, tokens_at_last + (now − last) × r)   # lazy refill: no timers
allowed = tokens ≥ 1
if allowed: tokens −= 1
remaining   = floor(tokens)
retry_after = allowed ? 0 : ceil((1 − tokens) / r)    # seconds, min 1 when sent
reset_after = ceil((B − tokens) / r)
```

- **Lazy refill.** The bucket is refilled when a request reads it, using the elapsed time. Nothing runs in the background.
- **Time source.** The in-memory version uses .NET's `TimeProvider`, a monotonic clock. Tests swap in `FakeTimeProvider` and control time exactly, so no test ever needs `Thread.Sleep`.
- **Memory.** In-memory buckets that have been idle for more than 10 minutes are evicted.
- **Why a token bucket?** It allows short bursts but enforces the average rate. It has no window boundary, so the fixed window's 2× burst cannot happen. It needs only two numbers per bucket. See [ADR-0002](adr/0002-token-bucket.md).

### 9.3 Fixed window (deliberately flawed, for experiment E2)

- `count[key, floor(now / W)]++` and allow while `count ≤ limit`, where `limit = r × W`.
- With `W = 10 s` and the free plan (100 requests per window), a client can send 100 requests at second 9.9 and 100 more at second 10.1. That is 200 requests in 0.2 s, against a stated rate of 10/s.

### 9.4 Redis token bucket (milestone M5)

- **Key:** `flux:rl:{<merchantId>}:<routeGroup>`. The `{…}` *hash tag* would keep all of one merchant's keys on the same Redis Cluster node. We run a single node, so this only prepares for the future.
- **Storage:** a Redis hash with fields `tokens` (float) and `ts` (milliseconds).
- **One Lua script** (`token_bucket.lua`, loaded once, then called with `EVALSHA`) does read → refill → decide → write → set expiry. Redis runs a script with nothing else interleaved, so two gateway instances can never both read "1 token left" and both spend it (a *read-modify-write race*).
- **Clock:** the script calls Redis `TIME`, so **Redis's clock is the only clock**. If each gateway passed in its own time, clock skew between instances would add or remove tokens.
- **Expiry:** `PEXPIRE key ceil(B / r × 1000) + 1000`. After that long a bucket would be full anyway, so deleting it changes nothing and saves memory.
- **Return values:** Redis truncates Lua numbers to integers, so the script returns `{allowed (0/1), remaining, retry_after_ms, reset_after_ms}` as integers.

### 9.5 When Redis is unavailable — per-route failure policy

The full reasoning is in [ADR-0005](adr/0005-per-route-failure-policy.md).

| Route group | Policy | What the client sees |
|-------------|--------|----------------------|
| `payments` | **Fail open, with a local fallback.** Each instance switches to its own in-memory bucket, sized at `plan limit ÷ AssumedInstances` (default 3, rounded up, minimum 1). | Normal `2xx`/`429`, plus `X-RateLimit-Degraded: true` |
| `refunds` | **Fail closed.** | `503 rate_limiter_unavailable` with `Retry-After: 5` |

**Why they differ:**
- A payment is money *coming in*, often with the merchant's customer waiting at checkout. Rejecting it loses revenue and trust. The fallback still bounds the traffic, so "open" does not mean "unlimited".
- A refund is money *going out*. It is a more attractive target for abuse and rarely urgent, so a short refusal is the safer error.

**How a failure is detected:**
- Each Redis call has a **50 ms timeout**. The StackExchange.Redis default is 5 s, and with that default an outage would add 5 s to every request. Experiment E3 demonstrates this.
- A **circuit breaker** opens when ≥50% of at least 20 calls fail within 10 s. While it is open, the gateway uses the fallback immediately, without waiting for a timeout. After 5 s it lets one trial call through.
- The gateway must **start successfully even if Redis is down** (`AbortOnConnectFail = false`).

## 10. Data model (PostgreSQL)

Managed with EF Core migrations ([ADR-0001](adr/0001-stack-dotnet-yarp.md)).

```sql
create table plans (
  id            text primary key,              -- 'free', 'pro'
  display_name  text not null
);

create table plan_route_limits (
  plan_id            text not null references plans(id),
  route_group        text not null check (route_group in ('payments', 'refunds')),
  refill_per_second  numeric(10,3) not null check (refill_per_second > 0),
  burst_capacity     integer not null check (burst_capacity > 0),
  primary key (plan_id, route_group)
);

create table merchants (
  id          uuid primary key,
  name        text not null,
  plan_id     text not null references plans(id),
  status      text not null default 'active' check (status in ('active', 'suspended')),
  created_at  timestamptz not null default now()
);

create table api_keys (
  id           uuid primary key,
  merchant_id  uuid not null references merchants(id),
  key_prefix   text not null,                  -- 'flux_3kX9vQ2' (safe to display)
  key_hash     bytea not null unique,          -- SHA-256 of the full key
  created_at   timestamptz not null default now(),
  revoked_at   timestamptz null
);
create index ix_api_keys_merchant on api_keys (merchant_id);
```

**Seed data:**
- Plans and limits come from a migration and exist in every environment.
- Demo merchants (`quiet-shop` on pro, `noisy-corp` on free, `suspended-co`) and their fixed dev keys are created **only when `ASPNETCORE_ENVIRONMENT=Development`**. If dev seeding is enabled in Production, the gateway refuses to start. This matters because the repo is public, so the dev keys are public too.

## 11. Operations surface

### 11.1 Admin API

Every admin endpoint requires the `X-Admin-Token` header (compared in constant time). The admin API is reachable only on the tailnet.

| Method + path | Body | Result |
|---------------|------|--------|
| `POST /admin/merchants` | `{ name, plan }` | `201 { merchantId, apiKey, keyPrefix }`. **The key is shown only in this response.** |
| `GET /admin/merchants/{id}` | — | Merchant, plan and keys (prefixes only) |
| `PATCH /admin/merchants/{id}` | `{ plan?, status? }` | `200`. Takes effect within the cache TTL (≤ 60 s). |
| `POST /admin/merchants/{id}/keys` | — | `201 { keyId, apiKey, keyPrefix }` |
| `DELETE /admin/keys/{keyId}` | — | `204`. Sets `revoked_at`; takes effect within ≤ 60 s. |

### 11.2 Health, metrics and logs

- **`GET /healthz`** (liveness): `200` if the process is running. Nothing else is checked.
- **`GET /readyz`** (readiness): `200` once plans have loaded from PostgreSQL at least once.
  - Readiness deliberately **does not check Redis**. If it did, a Redis outage would mark every gateway unready, and the load balancer would remove them all, a total outage. That would defeat the fail-open policy.
- **`GET /metrics`** (Prometheus text format):
  - `flux_requests_total{route_group, outcome}`, where outcome is one of `allowed`, `limited`, `unauthorized`, `forbidden`, `limiter_unavailable`, `not_found`
  - `flux_gateway_overhead_seconds` (histogram): total time minus time spent waiting on the upstream
  - `flux_ratelimiter_duration_seconds{algorithm}` (histogram)
  - `flux_ratelimiter_fallback_total{route_group, policy}`
  - `flux_ratelimiter_circuit_state` (gauge)
  - **No `merchant_id` label.** Every distinct label value creates a new time series, which at Razorpay's scale would mean millions of series (*cardinality explosion*). Per-merchant latency in experiments is measured on the client side, with k6 tags.
- **`Server-Timing` response header:** `gw;dur=0.84, upstream;dur=5.21` (milliseconds). This lets k6 record gateway overhead for each individual request.
- **Logs:** structured JSON to stdout, including `requestId`, `merchantId`, `routeGroup`, `outcome` and `keyPrefix`. Logs never contain full keys or request bodies.

## 12. Configuration

Settings use ASP.NET Core's normal configuration (`appsettings.json`), overridden by environment variables. `__` in a variable name means a nested key. Secrets come only from environment variables or Kubernetes Secrets and are never committed.

| Key | Default | Notes |
|-----|---------|-------|
| `ConnectionStrings__Postgres` | — | Required |
| `ConnectionStrings__Redis` | — | Required when the algorithm is `RedisTokenBucket` |
| `RateLimiting__Algorithm` | `InMemoryTokenBucket` | Also `InMemoryFixedWindow` or `RedisTokenBucket` |
| `RateLimiting__FixedWindow__WindowSeconds` | `10` | Experiment E2 only |
| `RateLimiting__Redis__TimeoutMs` | `50` | Per-call timeout |
| `RateLimiting__Fallback__AssumedInstances` | `3` | Divides limits in fail-open mode |
| `RateLimiting__FailurePolicy__payments` | `FailOpen` | Per route group |
| `RateLimiting__FailurePolicy__refunds` | `FailClosed` | Per route group |
| `ApiKeys__CacheTtlSeconds` | `60` | |
| `ApiKeys__NegativeCacheTtlSeconds` | `10` | |
| `ApiKeys__MaxCacheEntries` | `100000` | Cap on negative-cache entries (added in M2) |
| `Database__MigrateOnStartup` | `false` | `true` in Development. Production uses the migration Job (§17) (added in M2) |
| `Database__SeedDevData` | `false` | `true` in Development. The gateway refuses to start if it's `true` elsewhere (added in M2) |
| `Admin__Token` | — | Secret. If missing, the admin API is disabled. |
| `ReverseProxy__Clusters__payments__Destinations__d1__Address` | — | YARP upstream address |
| `ReverseProxy__Clusters__refunds__Destinations__d1__Address` | — | YARP upstream address |
| `Backend__ServiceName` | — | `payments` or `refunds` (backend only) |
| `Backend__LatencyMs` | `5` | Backend only |

**Fail fast:** the gateway refuses to start if a required setting is missing or a value is invalid (Field Guide §2.18).

## 13. Non-functional targets

These are **targets to verify, not results.** Reports will state actual numbers together with machine specs.

| # | Target | Measured in |
|---|--------|-------------|
| N1 | Gateway overhead ≤ 2 ms at p50 and ≤ 10 ms at p99, at 500 req/s on the local machine, with the Redis limiter | P3 |
| N2 | With 3 instances and Redis, a merchant's allowed count over 60 s is within ±5% of `B + r × 60` | P1 |
| N3 | The quiet merchant's p95 latency rises by ≤ 10% while the noisy merchant sends 100× its limit | P2 |
| N4 | During a Redis outage, no request waits more than ~50 ms extra. Payments keep flowing in degraded mode; refunds get a `503` in < 100 ms. Recovery is automatic once Redis returns. | P4 |
| N5 | On the server, the whole `flux` namespace stays within 1.5 GiB of memory limits and 2 GiB of disk | M7 |
| N6 | No plaintext API key stored or logged. Clients cannot spoof `X-Flux-*` headers. Backends are unreachable from outside the cluster or Compose network. | Tests + M7 |

## 14. Testing strategy

| Layer | Tooling | Examples |
|-------|---------|----------|
| Unit | xUnit v3 (on Microsoft.Testing.Platform) + `FakeTimeProvider` | Token-bucket maths at exact instants; the fixed-window boundary; header values; key generation and hashing; route mapping |
| Integration | xUnit + Testcontainers (real PostgreSQL and Redis) + `WebApplicationFactory` | Full request path; 401/403/429 contract; Lua script correctness; 500 parallel requests against Redis allow **exactly** `B`; Redis container stopped → payments degrade, refunds `503` |
| Contract | Integration tests that assert headers and Problem Details bodies | Every row of the §7.4 table |
| Load / experiments | k6 (Docker image) | §15 |

**Rules:**
- Tests use real PostgreSQL and Redis, not mocks, for anything that touches them (Field Guide §34, "Mock Everything").
- No test sleeps on the wall clock.
- CI runs the unit and integration suites on every push and pull request.

## 15. Experiments (Break → Prove)

Each experiment has a hypothesis, a reproducible command and a report in `docs/reports/`. Every report records the machine specs and the exact commands used. Latency experiments use k6's **constant-arrival-rate** executor. Otherwise a slow server makes the load generator slow down too, which hides the very latency we want to measure (*coordinated omission*).

| ID | Phase | Setup | Hypothesis / what we measure |
|----|-------|-------|------------------------------|
| E1 | Break | 3 gateways, `InMemoryTokenBucket`, nginx round-robin. `noisy-corp` (free: 10/s) sends 100 req/s for 60 s. | Allowed ≈ 3 × 10/s, because each instance has its own bucket. Measure the actual multiple. |
| E2 | Break | 1 gateway, `InMemoryFixedWindow` (W = 10 s, limit 100). A script sends 100 requests just before a window boundary and 100 just after. | ≈ 200 requests allowed within about 200 ms, double the quota. |
| E3 | Break | `RedisTokenBucket` with **default** client timeouts and no policy. Stop Redis mid-run. | Latency jumps to around 5 s and/or 500 errors appear. Record what actually happens. |
| P1 | Prove | E1 again, with `RedisTokenBucket`. | Allowed count within ±5% of quota (N2). |
| P2 | Prove | `quiet-shop` at 20 req/s and `noisy-corp` at 1000 req/s together, compared with `quiet-shop` alone. | Quiet merchant's p95 is flat (N3). Also note any rise in gateway CPU: rate limiting protects the *backends*, not the gateway's own CPU. |
| P3 | Prove | 500 req/s, k6 → backend directly vs. k6 → gateway → backend, plus `Server-Timing`. | Gateway overhead p50/p99 (N1), measured two independent ways. |
| P4 | Prove | E3 again with timeouts, the circuit breaker and per-route policies. | Matches N4. Record the timeline of degradation and recovery. |

**Where the numbers come from:**
- Official numbers come from the laptop's Docker Compose environment, with specs recorded. Docker Desktop on Windows runs inside a WSL2 virtual machine, so absolute numbers include some virtualisation overhead. The *comparisons* within one experiment are what matter.
- After M7, P1–P4 can optionally be rerun on the home server.
- Caveat: k6 and the system under test share CPUs. We cap k6's CPUs in Compose and note this in every report.

## 16. Environments

| Environment | What runs | How |
|-------------|-----------|-----|
| **Local** | PostgreSQL, Redis, payments, refunds, and 1 gateway (`single` profile) or 3 gateways + nginx (`cluster` profile). k6 on demand (`bench` profile). | `docker compose --profile <p> up`. The gateway can also run from the IDE with `dotnet run` against Compose's databases. |
| **CI** | GitHub-hosted Ubuntu runners | Build → unit + integration tests (Testcontainers) → build images → push to GHCR on `main` → deploy |
| **Home server** | k3s namespace `flux` on Megatron | Deployed only by GitHub Actions (§17) |

## 17. Deployment design (implemented in M7; summarised now so nothing surprises us)

This follows the same pattern Slate already uses successfully ([ADR-0006](adr/0006-deploy-k3s-actions-tailscale.md)).

```mermaid
flowchart LR
    Dev["git push main"] --> CI["GitHub Actions<br/>build + test"]
    CI --> GHCR["ghcr.io/kar6677thik/flux-*:&lt;sha&gt;"]
    CI -->|"tailscale/github-action (tag:ci)"| TS["Tailnet"]
    TS -->|"scp bundle + ssh"| S["Megatron"]
    S -->|"sudo -n /usr/local/sbin/flux-deploy"| K["k3s namespace flux"]
    K -.->|"pull image"| GHCR
```

**Workflow and images:**
- **Images:** `ghcr.io/kar6677thik/flux-gateway:<git-sha>` and `ghcr.io/kar6677thik/flux-backend:<git-sha>`. Tags are immutable, so a deploy always means "exactly this commit". The packages are public, so no pull secret is needed (still to verify in M7).
- **Manifests:** `deploy/k3s/` (Kustomize). The workflow fills in the image SHA, copies the bundle, and calls a narrow sudo helper. The helper applies **only** to namespace `flux` and waits for `kubectl rollout status`.

**What runs in the `flux` namespace:**
- **Workloads:**
  - Gateway: Deployment, 3 replicas.
  - Payments and refunds: one Deployment each.
  - PostgreSQL: StatefulSet with a PVC on the k3s `local-path` StorageClass.
  - Redis: Deployment with no persistence, because buckets are disposable.
- **NetworkPolicy:** only the gateway may talk to the backends, PostgreSQL and Redis.

**Process details:**
- **Migrations:** a separate EF Core migration-bundle Job runs **before** the gateway rollout. If all 3 replicas migrated on startup, they would race each other, and the schema would be changed from inside the request-serving process (Field Guide §22.21).
- **Secrets:** GitHub Actions secrets → a Kubernetes Secret manifest rendered in the runner's temp folder → applied by the helper → deleted. Nothing secret is committed, and nothing is changed by hand on the server.
- **Exposure:** tailnet only. Tailscale Serve HTTPS on its **own port** (planned: `:8443`) → gateway NodePort. Slate keeps `/` on `:443`.
- **Smoke test:** after deploying, the workflow calls `/readyz` and makes one authenticated request with a smoke-test merchant key.

**One-time steps only you can do** (we need sudo or account access; listed now so they're not a surprise):

1. ✅ **Free disk on Megatron.** *Done 2026-10-15: `/` is now 59% used (51 GB free).* It was 83% full. Kubelet's default image garbage collection starts at 85% and eviction at 85–90%, which could evict pods, **including Slate's**. Aim for ≥ 30% free before M7.
2. **Add GitHub secrets to the Flux repo:**
   - `TS_OAUTH_CLIENT_ID` and `TS_OAUTH_SECRET` (Slate's `tag:ci` client can be reused)
   - `HOME_SERVER_HOST`, `HOME_SERVER_USER`, `HOME_SERVER_KNOWN_HOSTS`
   - `HOME_SERVER_SSH_KEY` (a **new** key for Flux)
   - `FLUX_POSTGRES_PASSWORD`, `FLUX_ADMIN_TOKEN`, `FLUX_SMOKE_API_KEY`
3. **Authorise the deploy key:** add the new key's public half to `~/.ssh/authorized_keys` on Megatron.
4. **Install the deploy helper:** review and install `/usr/local/sbin/flux-deploy` plus one `sudoers` line (written in M7).
5. **Expose the gateway:** run `tailscale serve --bg --https=8443 http://127.0.0.1:<nodeport>` once.

## 18. Repository layout (target)

```
Flux/
├── README.md                     # what Flux is, architecture diagram, how to run, results
├── CLAUDE.md                     # conventions for AI-assisted sessions
├── Flux.slnx, global.json, Directory.Build.props, Directory.Packages.props
├── src/
│   ├── Flux.Gateway/             # YARP + auth + rate limiting + admin + metrics
│   └── Flux.Backend/             # fake payments/refunds service
├── tests/
│   ├── Flux.Backend.Tests/
│   ├── Flux.Gateway.UnitTests/       # from M2 (key format, cache TTLs)
│   └── Flux.Gateway.IntegrationTests/
├── deploy/
│   ├── compose/                  # docker-compose.yml, nginx.conf
│   └── k3s/                      # kustomize manifests, flux-deploy helper
├── bench/k6/                     # experiment scripts
├── .github/workflows/            # ci.yml, deploy.yml
└── docs/
    ├── README.md                 # reading order
    ├── spec.md                   # this file
    ├── glossary.md
    ├── adr/                      # architecture decision records
    ├── journal/                  # one entry per milestone: what, why, when
    ├── concepts/                 # one note per concept (Field Guide template)
    └── reports/                  # experiment reports with numbers
```

## 19. Milestones

Each milestone is a branch (`m<N>-<slug>`) and a pull request into `main`. Nothing is pushed or merged without your OK. CI must be green. **Every milestone produces a journal entry and its concept notes.**

| M | Name | Builds | Done when | Concept notes it adds |
|---|------|--------|-----------|----------------------|
| M0 | Specification | This document, ADRs, glossary, journal 00 | You have reviewed and agreed it | — |
| M1 | Skeleton | Solution, `Flux.Backend`, a gateway that only passes traffic through (YARP), Dockerfiles, Compose (`single`), CI workflow | `curl` through the gateway reaches both backends; CI green | reverse-proxy, middleware-pipeline, docker-compose, github-actions-ci |
| M2 | Tenancy & auth | EF Core model + migrations, seed, API-key middleware with positive and negative cache, Problem Details, admin API | 401/403 contract tests pass; keys stored hashed | api-key-authentication, multi-tenancy, caching-and-negative-caching, ef-core-migrations, problem-details |
| M3 | In-memory token bucket | `IRateLimiter`, `InMemoryTokenBucket`, header contract, 429, metrics + `Server-Timing` | Unit tests at exact instants; contract tests for every header | token-bucket, rate-limit-headers, testing-time, metrics-and-cardinality |
| M4 | Break it | `InMemoryFixedWindow`, Compose `cluster` profile + nginx, k6 scripts, **E1 and E2** with reports | Both reports show the failure with numbers | fixed-and-sliding-windows, load-balancing, load-testing-with-k6, coordinated-omission |
| M5 | Redis + resilience | `token_bucket.lua`, `RedisTokenBucket`, `ResilientRateLimiter` (timeout, breaker, per-route policy), **E3 then P4** | Concurrency test allows exactly `B`; Redis-down tests pass; E3 and P4 reports | redis-basics, lua-atomicity, clock-skew, timeouts-and-circuit-breakers, fail-open-vs-fail-closed |
| M6 | Prove it | **P1, P2, P3** reports, final README (diagram, results, résumé line with **measured** numbers only) | All N1–N4 measured and honestly reported | latency-percentiles, noisy-neighbour-isolation, benchmarking-methodology |
| M7 | Deploy | k3s manifests, `flux-deploy` helper, `deploy.yml`, smoke test, runbook | A push to `main` → running on Megatron → smoke test green | containers-and-registries, kubernetes-basics-on-k3s, continuous-deployment, secrets-management, tailscale-serve |

**Stretch goals, only after M7:** a sliding-window-counter algorithm, per-merchant limit overrides, a per-IP limit for unauthenticated traffic, and a comparison with ASP.NET Core's built-in `System.Threading.RateLimiting`.

## 20. Field Guide coverage

| Field Guide § | Where in Flux |
|---------------|---------------|
| 1 Problem framing | This spec, capacity targets, ADRs |
| 2 Principles | Fail fast on config (§12); least privilege in deployment (§17) |
| 3 Patterns | Strategy (`IRateLimiter`), Decorator (`ResilientRateLimiter`), Proxy (the gateway itself) |
| 7 API design | Gateway routing, Problem Details, correlation IDs, header contract |
| 8 Identity | API-key authentication, authentication vs. authorization, tenant isolation |
| 9 Data modelling | Plans, merchants, keys, constraints, migrations |
| 12 Caching | Key cache, negative cache, TTL trade-off |
| 14 Concurrency | Per-bucket locking in memory; read-modify-write race; atomic Lua |
| 15 Networking | Reverse proxy, L4 vs. L7 load balancing, keep-alive |
| 16 Distributed systems | Shared state across instances, clock skew, partial failure |
| 17 Resilience | Token bucket, timeouts, circuit breaker, fail open vs. closed, liveness vs. readiness |
| 20 Tenancy | Noisy-neighbour isolation, per-tenant quotas |
| 21–22 Containers, CI/CD | Docker, k3s, GHCR, GitHub Actions, migration Job |
| 23 Testing | Unit, integration (Testcontainers), contract, load |
| 24 Observability | RED metrics, histograms, cardinality, structured logs |
| 25 Security | Hashed keys, header spoofing, constant-time comparison, secret handling |
| 27 Performance | Percentiles, coordinated omission, overhead measurement |

## 21. Risks and known gotchas

| Risk | Mitigation |
|------|------------|
| A k8s Service balances *connections* (L4), not requests. k6 reuses keep-alive connections, so traffic can pin to one pod and **hide** the 3× bug on k3s. | Run E1 locally behind nginx, which balances per request (L7). Explain the difference in the load-balancing note. |
| StackExchange.Redis waits 5 s by default when Redis is down | This is exactly what E3 shows. M5 sets timeouts to 50 ms. |
| Redis truncates Lua numbers to integers | The script returns integer milliseconds (§9.4). |
| Load generator and system share CPUs | Cap k6's CPUs; note it in reports; compare within one run. |
| ~~The server disk is 83% full → kubelet eviction~~ | ✅ Freed to 59% used on 2026-10-15 (§17, step 1). Recheck before M7. |
| Public repo | No secrets in git. Dev keys are refused in Production. Secret scanning is enabled in CI. |
| Plan or key changes take up to 60 s to apply | Documented trade-off (§8). The admin API states it. |

## 22. Still open (and when each gets decided)

| Item | Decided in |
|------|-----------|
| ~~Exact NuGet and container image versions~~ | ✅ M1: `Directory.Packages.props` (YARP 2.3.0, xUnit v3 4.0.2, Mvc.Testing 10.0.13); images `dotnet/sdk:10.0` / `aspnet:10.0`; deployments pin by git SHA (M7) |
| Metrics library: OpenTelemetry Prometheus exporter or `prometheus-net` | M3 |
| Free NodePort and final Tailscale Serve port | M7 |
| Whether GHCR packages need to be made public manually | M7 |
| Whether to rerun the experiments on Megatron | After M6, your call |
