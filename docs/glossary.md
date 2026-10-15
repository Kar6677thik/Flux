# Glossary

These are the words used across Flux docs and code. The list grows with every milestone; each term notes where it first matters.

| Term | Meaning | First matters |
|------|---------|---------------|
| **Admission** | The moment the gateway decides to let a request through to a backend. Tokens are spent at admission. | M3 |
| **API gateway** | A single entry point in front of backend services that handles cross-cutting concerns: authentication, rate limiting, routing, headers. | M1 |
| **API key** | A long random secret that identifies a merchant. Flux stores only its SHA-256 hash. | M2 |
| **Bucket** | The state of one `(merchant, route group)` pair: how many tokens are left and when they were last refilled. | M3 |
| **Burst capacity (`B`)** | The most tokens a bucket can hold, which is also the most requests allowed in one instant. | M3 |
| **Cardinality** | The number of distinct values a metric label can take. Every combination becomes its own time series, so high cardinality makes monitoring expensive. | M3 |
| **Central package management** | Declaring every NuGet version once, in `Directory.Packages.props`, so all projects use the same versions. | M1 |
| **Circuit breaker** | A wrapper that stops calling a failing dependency for a while after repeated failures, so callers fail fast instead of waiting on timeouts. | M5 |
| **Clock skew** | Two machines disagreeing about the current time. | M5 |
| **Coordinated omission** | A load-testing error: a load generator that waits for slow responses sends fewer requests, so slowness hides itself. Avoided with constant-arrival-rate load. | M4 |
| **Fail open / fail closed** | When a safety check can't run, *open* lets traffic through and *closed* blocks it. | M5 |
| **Fixed window** | A limiter that counts requests per calendar slot (e.g. per 10 s). It can be gamed at slot boundaries. | M4 |
| **Fallback limiter** | The per-instance in-memory bucket used for `payments` while Redis is unavailable. | M5 |
| **GHCR** | GitHub Container Registry, where CI pushes Flux's images. | M7 |
| **Hash tag (Redis)** | The `{…}` part of a Redis key. In Redis Cluster, only that part decides which node owns the key. | M5 |
| **k3s** | A lightweight Kubernetes distribution. It already runs on Megatron. | M7 |
| **L4 / L7 load balancing** | L4 balances TCP *connections*; L7 understands HTTP and balances *requests*. | M4 |
| **Launch profile** | A named way to `dotnet run` a project (port, environment variables), stored in `Properties/launchSettings.json`. It only affects `dotnet run`, never containers. | M1 |
| **Lazy refill** | Computing the tokens earned since the last request when a bucket is read, instead of adding them on a timer. | M3 |
| **Liveness / readiness** | *Liveness*: should this process be restarted? *Readiness*: should it receive traffic right now? | M1 |
| **Lua script (Redis)** | A small program Redis runs atomically. No other command runs while it executes. | M5 |
| **Megatron** | The home server (on the tailnet at 100.91.115.46). | M7 |
| **Merchant** | A tenant: a business calling the payments API. It has one plan and one or more API keys. | M2 |
| **Middleware** | A step in ASP.NET Core's request pipeline. Each one can act on the request and then call the next, or stop and respond. | M1 |
| **Microsoft.Testing.Platform (MTP)** | The test runner `dotnet test` uses in .NET 10 (replacing VSTest). xUnit v3 test projects are executables that include it. | M1 |
| **Multi-stage build** | A Dockerfile with a heavy build stage and a slim runtime stage. Only the compiled output is copied into the final image. | M1 |
| **Negative caching** | Caching "this key does not exist", so floods of bad keys don't each reach the database. | M2 |
| **Noisy neighbour** | One tenant whose heavy traffic degrades service for other tenants. | M6 |
| **p50 / p95 / p99** | Latency percentiles. p99 = 1 in 100 requests is slower than this value. | M4 |
| **Plan** | A named set of limits (free, pro), one limit per route group. | M2 |
| **Problem Details** | RFC 9457: the standard JSON shape for HTTP API errors. | M2 |
| **Read-modify-write race** | Two actors read the same value, both modify it, and one write overwrites the other. | M5 |
| **Refill rate (`r`)** | Tokens added per second, which is the sustained request rate the plan allows. | M3 |
| **Reverse proxy** | A server that receives client requests and forwards them to backend servers, hiding the backends from the client. | M1 |
| **Route group** | The unit limits are applied to: `payments` or `refunds`. Several HTTP routes share one group. | M2 |
| **Tailnet** | Your private Tailscale network. Only your devices can reach services exposed on it. | M7 |
| **Tenant** | One customer in a shared (multi-tenant) system. In Flux, a merchant. | M2 |
| **Token bucket** | A limiter in which requests spend tokens and tokens refill at a steady rate, up to a cap. | M3 |
| **YARP** | "Yet Another Reverse Proxy": Microsoft's reverse-proxy library for ASP.NET Core. | M1 |
