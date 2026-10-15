# Journal 01 — Skeleton (M1)

- **Date:** 2026-10-15
- **Milestone:** M1
- **Branch:** `m1-skeleton`

## What we did
1. **Repository foundations**
   - [`global.json`](../../global.json) pins the SDK to `10.0.401` and opts into the new test platform.
   - [`Directory.Build.props`](../../Directory.Build.props) sets shared compiler rules: nullable on, warnings are errors.
   - [`Directory.Packages.props`](../../Directory.Packages.props) pins every NuGet version in one place.
   - [`.gitattributes`](../../.gitattributes) keeps line endings LF for files that run on Linux.
   - [`Flux.slnx`](../../Flux.slnx) is the new XML solution format.
2. **[`Flux.Backend`](../../src/Flux.Backend/)**: one fake service that runs as `payments` *or* `refunds` (ADR-0007).
   - Rejects anything without `X-Flux-Merchant-Id` (`400 missing_merchant_context`).
   - Validates input and returns Razorpay-style ids (`pay_…`, `rfnd_…`).
   - Refuses to start with an invalid `Backend:ServiceName`.
3. **[`Flux.Gateway`](../../src/Flux.Gateway/)**: a pass-through YARP reverse proxy implementing spec §5 steps 1, 2, 3, 6 and 7:
   - correlation ids;
   - stripping client-supplied `X-Flux-*` headers;
   - 4 routes rewriting `/v1/x` → `/x`;
   - a Problem Details `404` for unknown routes, `502` for an unreachable backend and `504` for a slow one;
   - `/healthz` and `/readyz`.
4. **Tests**, 26 in total, all passing:
   - [`Flux.Backend.Tests`](../../tests/Flux.Backend.Tests/) (10) checks the fake services' contract.
   - [`Flux.Gateway.IntegrationTests`](../../tests/Flux.Gateway.IntegrationTests/) (16) runs the real gateway against a real echo server.
5. **Containers**:
   - multi-stage Dockerfiles that run as a non-root user;
   - [`docker-compose.yml`](../../deploy/compose/docker-compose.yml) with the `single` profile.
6. **CI**: [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs build + test, then builds both images, on every push and PR.

## Why this order (why *now*)
- **A skeleton end to end before any feature.** Every later milestone plugs into a slot that already exists: auth and rate limiting go between header stripping and the proxy. If the plumbing (proxying, containers, CI) is broken, it breaks *now*, while there's little code to suspect (Field Guide §1.11, risk-driven development).
- **Security-relevant steps first.** Header stripping costs ten lines, but would be easy to forget once auth exists. Its test pins the behaviour permanently.
- **CI before features.** From M2 on, every PR is checked automatically. Adding CI later means first fixing everything that quietly broke without it.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [Reverse proxy (and YARP)](../concepts/reverse-proxy.md) | §7.5, §15.7, §15.12–13 |
| [The middleware pipeline](../concepts/middleware-pipeline.md) | §3 (Chain of Responsibility), §7.19, §25.4 |
| [Containers, Dockerfiles, Compose](../concepts/docker-compose.md) | §21.1–2, §25.28, §15.18 |
| [CI with GitHub Actions](../concepts/github-actions-ci.md) | §22.1, §22.11, §22.24–25 |

Also new in the [glossary](../glossary.md): central package management, launch profile, multi-stage build, Microsoft.Testing.Platform.

## Decisions made during M1
| Decision | Why |
|----------|-----|
| xUnit **v3** (4.0.2) on **Microsoft.Testing.Platform** | .NET 10's `dotnet test` no longer runs xUnit v3 through VSTest. MTP is the supported path, and it needs fewer packages. |
| `Flux.Backend.Tests` added; gateway *unit* tests deferred to M3 | M1's gateway behaviour is all wiring, so integration tests are the right tool. Pure logic (the token bucket maths) arrives in M3. |
| Local ports in `launchSettings.json`, not `appsettings.Development.json` | `appsettings.*.json` is also read *inside containers*. A `localhost` URL there would make a Development container unreachable. Launch profiles only affect `dotnet run`. |
| The backends publish no ports in Compose | Mirrors the isolation planned for k3s (spec N6): from outside, the only door is the gateway. |
| Request ids are 32 hex characters (`Guid "N"`) | They pass our own validation rule (`[A-Za-z0-9_-]`, which `TraceIdentifier`'s `:` would fail), and they're unique without coordination. |
| `DELETE /v1/payments/x` → `404`, not `405` | YARP doesn't match the route at all, so it falls through to the 404 fallback. A proper 405 isn't worth the complexity now. Noted in the reverse-proxy note. |

## How to verify it yourself
```bash
dotnet build -c Release
```
```bash
dotnet test
```
Run the three processes from your IDE, or in three terminals:
```bash
dotnet run --project src/Flux.Backend --launch-profile payments
```
```bash
dotnet run --project src/Flux.Backend --launch-profile refunds
```
```bash
dotnet run --project src/Flux.Gateway
```
Then try these. First, through the gateway with a forged merchant header: the gateway strips it, so the backend answers `400 missing_merchant_context`.
```bash
curl -i -X POST localhost:5100/v1/payments -H "Content-Type: application/json" -H "X-Flux-Merchant-Id: evil" -d '{"amount":50000}'
```
Straight to the backend, bypassing the gateway: `201`. This only works because you're on the backend's own machine. In Compose and k3s it isn't reachable.
```bash
curl -i -X POST localhost:5201/payments -H "Content-Type: application/json" -H "X-Flux-Merchant-Id: m_1" -d '{"amount":50000}'
```
An unknown route returns `404 route_not_found` with a `requestId`:
```bash
curl -i localhost:5100/v1/nope
```
Or the same in containers (Docker Desktop must be running), with the gateway on `:8080`:
```bash
docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
```

> **Why does every `/v1` call return 400 right now?** Nothing authenticates merchants yet, so the gateway never sets `X-Flux-Merchant-Id`, and the backends correctly refuse. M2 adds API-key auth, and from then on the same `curl` (with a key) returns `201`. That 400 is the system working as designed.

## Exercises
1. In [`Program.cs`](../../src/Flux.Gateway/Program.cs), swap the two `UseMiddleware` lines. Which test fails, and why? Swap them back.
2. Change `payments-get`'s transform to `{ "PathRemovePrefix": "/v1" }`. Is the behaviour the same? When would `PathPattern` be the better choice?
3. Why does `UpstreamErrorMiddleware` check `http.Response.HasStarted` before writing a body?
4. With Compose running, try `curl localhost:5201/healthz` from your laptop. Why does it fail, and why is that *good*?
5. Edit only `Program.cs` in `Flux.Gateway` and rebuild the image. Which Dockerfile steps run again, and which come from cache?

<details><summary>Answers</summary>

1. **None fails.** Header stripping only touches `X-Flux-*` and correlation only touches `X-Request-Id`, so today these two steps are independent. That's the useful lesson. Stripping must come before **auth** (M2) and anything else that reads `X-Flux-*`, and tests only protect the orderings they actually exercise. That's why M2 adds a test where a forged header meets real auth.
2. Same result for these routes. `PathPattern` is better when the upstream path isn't just "public path minus a prefix", e.g. `/v1/payments/{id}` → `/internal/payments/{id}`. It's also more explicit: you can read the upstream path straight off the config.
3. Once the first byte of a response has been sent, the status code and headers are on the wire and can't be changed. If YARP failed half-way through streaming a response, the only honest option is to abort the connection, not to append a JSON error to a half-sent body.
4. The backends publish no ports, so they exist only on Compose's private network. If a client could reach a backend directly, it could set `X-Flux-Merchant-Id` itself and skip auth and rate limiting entirely.
5. The `restore` layer stays cached, because only `.csproj` files feed it. `COPY src/Flux.Gateway/` and `publish` run again, and the runtime stage's `COPY --from=build` changes. The package download is skipped.
</details>

## What could go wrong
- **Docker Desktop not running** → `docker compose` fails with a pipe error. Start Docker Desktop first. (This session couldn't launch it, so the *Compose run* is still to be verified locally. CI verifies that the images build.)
- **Port 5100/5201/5202 already in use** → `dotnet run` fails to bind. Find the process with `netstat -ano | findstr :5100`.
- **SDK older than 10.0.401** → `global.json` makes the build fail with a clear message. Install a newer SDK; don't edit `global.json` to an older version.

## Next: M2 — Tenancy & auth
- PostgreSQL in Compose (and in tests via Testcontainers).
- EF Core model + migrations for plans, merchants and API keys.
- API-key middleware with positive and negative caching.
- The admin API.
- Readiness that waits for plans to load.

After M2, `curl -H "Authorization: Bearer flux_…"` gets a `201`.
