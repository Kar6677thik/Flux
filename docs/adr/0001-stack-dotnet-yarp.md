# ADR-0001: .NET 10 + YARP + EF Core for the gateway

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0

## Context
The project guide allows "ASP.NET Core or Python (choose most suitable)". A gateway sits on the hot path of every request, so the stack needs three things: a production-grade reverse proxy we can extend with our own middleware, async I/O, and good latency. The author already works in .NET (ASP.NET.Lab, Slate), and the .NET 10 SDK is installed both locally and in Slate's CI.

## Decision
- **Gateway and backends:** ASP.NET Core on **.NET 10** (LTS).
- **Proxying:** **YARP** (`Yarp.ReverseProxy`), Microsoft's reverse-proxy library. It plugs into the normal ASP.NET Core middleware pipeline, so auth and rate limiting are ordinary middleware placed before the proxy.
- **Database access:** **EF Core + Npgsql**. We use it for the schema, migrations and the admin API. The hot path does not query the database at all, because keys and plans are cached in memory (spec §8).
- **Redis:** **StackExchange.Redis**.
- **Tests:** **xUnit**, **Testcontainers**, `Microsoft.Extensions.TimeProvider.Testing`.

## Alternatives considered
| Option | Why not (now) |
|--------|---------------|
| Python (FastAPI + httpx proxy) | No mature, embeddable reverse proxy like YARP. More of the proxy plumbing would be hand-written, which teaches less about the actual problem. |
| nginx/OpenResty + Lua, or Kong | Closer to what many real gateways use, but most of the learning would become configuration rather than code. The guide also wants C# + xUnit. |
| Dapper instead of EF Core | Dapper is excellent for showing SQL explicitly. But we'd have to hand-roll migrations, and the deploy pipeline (migration Job) benefits from EF Core migration bundles. The hot path doesn't touch the DB, so EF Core's overhead doesn't matter here. |

## Consequences
- One language across gateway, backends, tests and tooling.
- YARP hides some proxy details (connection pooling, header forwarding). The M1 concept note opens these up so they don't stay magic.
- EF Core generates SQL for us, so the M2 note shows the generated SQL alongside the C#.

## Revisit when
Measured gateway overhead (experiment P3) is dominated by something YARP does and we can't configure it away.
