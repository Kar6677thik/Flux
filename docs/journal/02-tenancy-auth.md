# Journal 02 — Tenancy & auth (M2)

- **Date:** 2026-10-15
- **Milestone:** M2
- **Branch:** `m2-tenancy-auth`

## What we did
1. **The data model** (spec §10), in [`FluxDbContext`](../../src/Flux.Gateway/Data/FluxDbContext.cs):
   - tables `plans`, `plan_route_limits`, `merchants` and `api_keys`, with snake_case columns, `CHECK` constraints and a unique index on `key_hash`;
   - one EF Core migration, [`InitialSchema`](../../src/Flux.Gateway/Data/Migrations/). It also seeds the free and pro plans with the limits from spec §6;
   - [`DevSeed`](../../src/Flux.Gateway/Data/DevSeed.cs) adds `quiet-shop` (pro), `noisy-corp` (free) and `suspended-co`, each with a fixed public key. It runs **only in Development**; anywhere else the gateway refuses to start.
2. **API keys** ([`ApiKey`](../../src/Flux.Gateway/Auth/ApiKey.cs)): `flux_` + 43 base62 characters from 32 CSPRNG bytes. They're stored as SHA-256 plus a 12-character prefix and shown once.
3. **Authentication** ([`ApiKeyAuthMiddleware`](../../src/Flux.Gateway/Auth/ApiKeyAuthMiddleware.cs)), inside YARP's pipeline (spec §5, step 4):
   - `401 missing_api_key`, `401 invalid_api_key`, `403 merchant_suspended`;
   - sets `X-Flux-Merchant-Id` and `X-Flux-Plan` for the backend, and **removes `Authorization`** before forwarding.
4. **Caching** ([`ApiKeyCache`](../../src/Flux.Gateway/Auth/ApiKeyCache.cs)):
   - found keys are cached for 60 s, unknown keys for 10 s, with a cap on the negative entries;
   - all times come from `TimeProvider`.
5. **The plan catalog** ([`PlanCatalog`](../../src/Flux.Gateway/Tenancy/PlanCatalog.cs) + [`PlanRefreshService`](../../src/Flux.Gateway/Tenancy/PlanRefreshService.cs)):
   - it loads at startup, retries every 2 s until the first load succeeds, then refreshes every 60 s;
   - `/readyz` returns `503` until the first load.
6. **The admin API** ([`AdminEndpoints`](../../src/Flux.Gateway/Admin/AdminEndpoints.cs)):
   - the five endpoints from spec §11.1, behind `X-Admin-Token` (hashed, then compared in constant time);
   - if no token is configured, the routes don't exist.
7. **The error contract, completed** ([problem-details](../concepts/problem-details.md)):
   - an exception handler maps bad JSON to `400` and bugs to `500 internal_error`;
   - five new codes appear in spec §7.4;
   - [ADR-0008](../adr/0008-auth-when-postgres-is-down.md) covers PostgreSQL outages.
8. **Tests: 76 in total, all passing** (M1 had 26):
   - [`Flux.Gateway.UnitTests`](../../tests/Flux.Gateway.UnitTests/) (new, 17): key format and hashing, and cache TTLs at exact instants with `FakeTimeProvider`.
   - [`Flux.Gateway.IntegrationTests`](../../tests/Flux.Gateway.IntegrationTests/) (49): the real gateway against **real PostgreSQL 17 in Testcontainers**. The M1 proxy tests now authenticate with the seeded quiet-shop key.
9. **Compose**:
   - a `postgres` service with a healthcheck; the gateway waits until it's healthy;
   - the gateway runs as Development, so it migrates and seeds itself;
   - fixed a latent M1 bug (see "What could go wrong").

After M2, the same `curl` that returned `400` in M1 returns `201`.

## Why this order (why *now*)
- **Identity before limits.** M3's rate limiter needs a bucket key `(merchantId, routeGroup)` and the merchant's plan. Both come from auth. Building the limiter first would have meant faking the merchant, and then rewriting the code that did the faking.
- **The data model is the most expensive thing to change later** (Field Guide §9). Getting the constraints and keys right before anything depends on them is cheap now. The `CASCADE` story below shows how wrong a default can be.
- **The cache arrives with the lookup, not after.** Without it, every request queries PostgreSQL, and M3–M6 would measure the database rather than the gateway. Adding the cache together with its TTL tests means the staleness trade-off is documented and tested from the first day it exists.
- **The admin API now, not "when needed".** Tests need to create merchants, and so will k6 in M4. Creating merchants through the real API means the tests exercise it constantly.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [API-key authentication](../concepts/api-key-authentication.md) | §8.1, §25.18–19, §25.25 |
| [Multi-tenancy](../concepts/multi-tenancy.md) | §8.13, §9.20, §20.15–16, §25.37 |
| [Caching and negative caching](../concepts/caching-and-negative-caching.md) | §12.1, §12.6, §12.11–12, §12.15 |
| [EF Core and schema migrations](../concepts/ef-core-migrations.md) | §9.5–9.9, §9.14, §22.21 |
| [Problem Details and the error contract](../concepts/problem-details.md) | §7.15–16, §25.17 |

Updated: [the middleware pipeline](../concepts/middleware-pipeline.md), because auth sits inside YARP's pipeline, not the main one. New in the [glossary](../glossary.md): authentication vs. authorization, cache-aside, cache penetration and stampede, CASCADE/RESTRICT, constant-time comparison, FakeTimeProvider, key prefix, Testcontainers, TTL, UUIDv7, `WWW-Authenticate`, and more.

## Decisions made during M2
| Decision | Why |
|----------|-----|
| [ADR-0008](../adr/0008-auth-when-postgres-is-down.md): PostgreSQL down → cached keys work, uncached keys get `503 auth_unavailable` | The spec didn't cover it, and the first test returned a bare `500`. A `401` would be a lie, and failing open is impossible without knowing the merchant. |
| Auth runs **inside YARP's pipeline** | Spec §5 puts route matching before auth. This way `/healthz` and `/admin` never ask for a merchant key, and unknown paths are `404`, not `401`. |
| `Authorization` is removed before forwarding | Backends don't need the secret, and every hop it travels is another place it can leak. |
| A **custom** cache with two maps, not `IMemoryCache` | Exact-instant TTL tests with `TimeProvider`, and a cap on the *negative* entries only: positive entries are bounded by real keys, negative ones by an attacker's imagination. |
| Every foreign key is `RESTRICT` | EF's default `CASCADE` meant deleting a plan would delete its merchants and their keys. We found it by reading the generated SQL. |
| `Database:MigrateOnStartup` / `Database:SeedDevData`, both off by default | Development turns them on. Production migrates with a Job (M7), and refuses dev seeding outright, because the dev keys are public. |
| Tests share **one** PostgreSQL container (an xUnit v3 *assembly fixture*), migrated once | Startup cost is paid once (~2 s). Tests stay independent by creating their own merchants. |
| Gateway unit-test project in M2, not M3 | The key format and cache TTLs are pure logic that deserves exact unit tests. The spec's layout now says "from M2". |
| `RouteHandlerOptions.ThrowOnBadRequest = true` | Without it, malformed JSON gets a body-less `400` in Production but an exception in Development. A Development-only test would never have caught the difference, so the test runs in both. |
| `Guid.CreateVersion7` for ids | Time-ordered, so primary-key inserts append to the index. |
| Compose PostgreSQL on host port **5433** | This laptop already runs a native PostgreSQL on 5432 (we found out when Compose couldn't bind it). |
| `CentralPackageTransitivePinningEnabled` + an EF Core pin | Npgsql asked for EF Core 10.0.4, the design package brought 10.0.13; the version conflict was a build warning, and warnings are errors. |

## How to verify it yourself
Build and test. Docker Desktop must be running: the integration tests start their own PostgreSQL container.
```bash
dotnet build -c Release
```
```bash
dotnet test
```
Start the stack (gateway on `:8080`, PostgreSQL on `127.0.0.1:5433`):
```bash
docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
```
No key → `401`, with the `WWW-Authenticate` header the HTTP spec requires:
```bash
curl -i localhost:8080/v1/payments/pay_1
```
The seeded pro merchant, still trying to forge a merchant id. The `201` shows quiet-shop's real id (`8d8688a8-…`), not `evil`:
```bash
curl -i -X POST localhost:8080/v1/payments -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT" -H "Content-Type: application/json" -H "X-Flux-Merchant-Id: evil" -d '{"amount":50000,"currency":"INR"}'
```
Suspended → `403 merchant_suspended`; unknown → `401 invalid_api_key`:
```bash
curl -s localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devSuspendedV3E3JkLSVkbisS2kfKQ5yn9BkiG9nic"
```
```bash
curl -s localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_0000000000000000000000000000000000000000000"
```
Create a merchant through the admin API (Development's public dev token). Copy the `apiKey`, because it's never shown again:
```bash
curl -s -X POST localhost:8080/admin/merchants -H "X-Admin-Token: dev-admin-token-public-do-not-use-in-prod" -H "Content-Type: application/json" -d '{"name":"acme","plan":"free"}'
```
See what the database actually stores: prefixes and hashes, no keys:
```bash
docker compose -f deploy/compose/docker-compose.yml exec postgres psql -U flux -d flux -c "select m.name, m.status, k.key_prefix, encode(k.key_hash,'hex') from api_keys k join merchants m on m.id = k.merchant_id;"
```
Prove that no full key was ever logged (expect `0`):
```bash
docker compose -f deploy/compose/docker-compose.yml logs gateway | grep -cE "flux_[0-9A-Za-z]{43}"
```
These are the results from this session's run:

| Command | Result |
|---------|--------|
| no key | `401` · `WWW-Authenticate: Bearer realm="flux"` · `code: missing_api_key` |
| quiet-shop `POST` with forged header | `201` · `"merchantId":"8d8688a8-84d7-45d7-9f4a-b4708d86bed0"` |
| suspended-co | `403` · `merchant_suspended` |
| unknown key | `401` · `invalid_api_key`; the log line shows only `flux_0000000` |
| `psql` | 4 rows, each a 12-character prefix and a 64-hex-digit hash |
| log grep | `0` |

To run the gateway from your IDE instead, start only the database, then use `dotnet run` (the Development config already points at `localhost:5433`):
```bash
docker compose -f deploy/compose/docker-compose.yml up -d postgres
```
```bash
dotnet run --project src/Flux.Gateway
```

## Exercises
1. Revoke a key with `DELETE /admin/keys/{id}` and keep calling with it. How long until you get a `401`? Which test proves the exact boundary, and why can that test be exact when a test using `Thread.Sleep` couldn't?
2. An attacker sends 1 million requests, each with a *different* well-formed random key. How many database queries does the negative cache save? What would actually help?
3. A popular merchant's cache entry expires and 500 of their requests arrive in the same millisecond. Count the database queries. Sketch a fix.
4. Why does a suspended merchant get `403` and not `401`? And why does a revoked key get exactly the same response as a key that never existed?
5. In `FluxDbContext`, remove one `.OnDelete(DeleteBehavior.Restrict)`, run `dotnet ef migrations add Probe --project src/Flux.Gateway --output-dir Data/Migrations`, and read the generated file. What changed, and what would `delete from plans where id = 'free'` have done? (Then `dotnet ef migrations remove --project src/Flux.Gateway`.)
6. `AdminTokenFilter` hashes both tokens before `FixedTimeEquals`. What would leak without the hashing?
7. Set `Database__SeedDevData=true` and `ASPNETCORE_ENVIRONMENT=Production` and start the gateway. What happens, and why is that the right behaviour for this repo in particular?

<details><summary>Answers</summary>

1. **Up to 60 s** on each gateway instance (the positive TTL). `A_revoked_key_keeps_working_until_the_cache_ttl_then_gets_401` replaces the gateway's `TimeProvider` with a `FakeTimeProvider`: `Advance(59 s)` → still `200`, `Advance(1 s)` → `401`. Time moves only when the test says so, so the boundary is exact and the test takes milliseconds. With `Thread.Sleep(60_000)` the test would be slow, and it would still be imprecise: was it 59.9 s or 60.1 s when the request landed?
2. **Essentially none.** Each key is new, so each one is a cache miss and a query. Negative caching only helps when the *same* missing key repeats. The cap stops memory growth, but not the queries. What helps: rate-limiting unauthenticated traffic per IP (a stretch goal); a Bloom filter of valid key hashes (it never says "no" to a real key and almost always says "no" to a random one, with no database); or putting the limit in front of auth. Spec §8 now says this explicitly.
3. **Up to 500.** Every request checks the cache, finds it expired, and queries. The fix is *single-flight*: keep a `ConcurrentDictionary<hash, Task<AuthenticatedMerchant?>>` of in-progress lookups. The first request starts the query and stores the `Task`; the other 499 await that same `Task`; remove it when it completes. (Watch out: a failed or cancelled `Task` must not be cached.) Field Guide §12.11.
4. A `401` means "I don't know who you are" (fix your credentials). A suspended merchant *is* identified, so the right answer is "I know who you are, and no" (`403`). For revoked versus unknown: telling a caller "this key used to be valid" confirms to someone holding a stolen, revoked key that they had a real one, and it helps nobody legitimate. Same response, same code path, nothing to learn.
5. The FK's `onDelete:` becomes `ReferentialAction.Cascade`. With cascade on `merchants.plan_id`, deleting the `free` plan deletes **every free merchant**. If the `api_keys` FK cascades too, their keys go with them, in one statement and without an error. With `RESTRICT`, PostgreSQL refuses the delete while any merchant uses the plan.
6. **The token's length.** `FixedTimeEquals` returns `false` immediately when the inputs differ in length, so response timing would reveal when a guess has the right length. SHA-256 outputs are always 32 bytes, so the comparison always takes the same path.
7. It refuses to start: `InvalidOperationException: Database:SeedDevData is enabled in the 'Production' environment…`. The repo is **public**, so the three dev keys are known to the whole internet. Seeding them in a real deployment would hand anyone a working merchant key. Failing at startup (Field Guide §2.18) means that mistake can't get past the first deploy. `Dev_seeding_outside_development_refuses_to_start` covers it.
</details>

## What could go wrong
- **Docker Desktop not running** → every integration test fails in its fixture with a Docker connection error. The unit tests still pass. Start Docker Desktop and rerun.
- **Port 5433 already in use** → Compose's `postgres` won't start. On this laptop 5432 was already taken by a native PostgreSQL, which is why Compose uses 5433. Find the owner with `netstat -ano | findstr :5433`.
- **Compose: `image "flux-backend:local": already exists`.** A latent M1 bug: both backend services built the same tag in parallel and raced to export it. CI never ran Compose, only `docker build`, so nobody saw it. Now only `payments` builds the image and `refunds` reuses it (`pull_policy: never`).
- **GitHub push protection rejects the push** (`GH013 … Stripe API Key`). This happened to M2's first push. A unit test used a made-up `sk_live_…` string as "another vendor's key format", and GitHub's scanner can't tell a fake that has Stripe's shape from a real key. The fix was to change the test string, *not* to click "allow secret": a habit of bypassing the scanner is exactly how a real key gets through one day. The flip side, worth noticing: our own `flux_` prefix is what would let a scanner recognise *our* keys too (see the [API-key note](../concepts/api-key-authentication.md)).
- **Changed the model but forgot the migration** → tests fail with "relation … does not exist" or a column error. Run `dotnet ef migrations add`, then **read the SQL** (`dotnet ef migrations script`).
- **Stale volume.** If you change `InitialSchema` itself (instead of adding a new migration), an existing `pgdata` volume still holds the old schema and history. Reset it with `docker compose -f deploy/compose/docker-compose.yml down -v`. Never edit a migration that has already run anywhere that matters; add a new one.
- **Data Protection warnings in the gateway's container logs** (`Storing keys in a directory … not persisted`). They're harmless today: Flux doesn't use cookies or antiforgery tokens, so nothing protected is lost on restart.

## Next: M3 — In-memory token bucket
- `IRateLimiter` and `InMemoryTokenBucket`, reading limits from the `PlanCatalog` built here and the merchant from `HttpContext.GetMerchant()`.
- The header contract: `X-RateLimit-*`, `429` + `Retry-After`.
- Unit tests at exact instants, with the same `FakeTimeProvider` technique as the cache tests.
- `/metrics` and `Server-Timing`, and choosing the metrics library (spec §22).

After M3, `noisy-corp` sending 25 requests in an instant gets 10 × `200` and 15 × `429`.
