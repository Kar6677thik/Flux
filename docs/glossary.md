# Glossary

These are the words used across Flux docs and code. The list grows with every milestone; each term notes where it first matters.

| Term | Meaning | First matters |
|------|---------|---------------|
| **Admission** | The moment the gateway decides to let a request through to a backend. Tokens are spent at admission. | M3 |
| **API gateway** | A single entry point in front of backend services that handles cross-cutting concerns: authentication, rate limiting, routing, headers. | M1 |
| **API key** | A long random secret that identifies a merchant. Flux stores only its SHA-256 hash. | M2 |
| **Assembly fixture** | An xUnit v3 fixture shared by every test in a test project. Flux uses one to start a single PostgreSQL container per test run. | M2 |
| **Authentication vs. authorization** | *Authentication* proves who the caller is (bad key → `401`). *Authorization* decides what they may do (suspended → `403`). | M2 |
| **Backlog (Redis client)** | Commands StackExchange.Redis holds while disconnected. The default queues them until reconnect or timeout; Flux uses `FailFast` (E3 showed why). | M5 |
| **Baseline (benchmark)** | The same measurement without the change under test (one gateway, no flood, direct to the backend), so the change's effect can be isolated. | M6 |
| **Bucket** | The state of one `(merchant, route group)` pair: how many tokens are left and when they were last refilled. | M3 |
| **Burst capacity (`B`)** | The most tokens a bucket can hold, which is also the most requests allowed in one instant. | M3 |
| **Cache penetration** | Lookups for data that doesn't exist always miss the cache and hit the database. Negative caching reduces it for *repeated* keys. | M2 |
| **Cache stampede** | Many requests miss the same expired entry at once and all query the database. Prevented with single-flight loading (not built yet). | M2 |
| **Cache-aside** | The application checks the cache, and on a miss loads from the database and stores the result itself. | M2 |
| **Cardinality** | The number of distinct values a metric label can take. Every combination becomes its own time series, so high cardinality makes monitoring expensive. | M3 |
| **CASCADE / RESTRICT** | What a foreign key does when the referenced row is deleted: delete the dependents too, or refuse. EF Core defaults required FKs to CASCADE; Flux uses RESTRICT. | M2 |
| **Central package management** | Declaring every NuGet version once, in `Directory.Packages.props`, so all projects use the same versions. | M1 |
| **Circuit breaker** | A wrapper that stops calling a failing dependency for a while after repeated failures, so callers fail fast instead of waiting on timeouts. | M5 |
| **Clock skew** | Two machines disagreeing about the current time. | M5 |
| **Closed loop / open loop (load)** | Closed: each virtual user waits for a response before sending the next request, so load drops when the server slows. Open: requests start on a schedule regardless. Flux measures with open loops. | M4 |
| **Consistent hashing** | Mapping keys to servers so that each key always lands on the same server, and adding or removing a server moves only a few keys. | M4 |
| **Constant arrival rate** | k6's open-loop executor: start N iterations per second, however long each one takes. | M4 |
| **Constant-time comparison** | Comparing secrets in a way that takes the same time wherever they differ, so timing reveals nothing (`CryptographicOperations.FixedTimeEquals`). | M2 |
| **Control run** | A run designed to test an *explanation* of a surprising result, changing one suspected cause and nothing else (P2's CPU burner). | M6 |
| **Coordinated omission** | A load-testing error: a load generator that waits for slow responses sends fewer requests, so slowness hides itself. Avoided with constant-arrival-rate load. | M4 |
| **Delta seconds** | A duration ("in 3 seconds") rather than a timestamp ("at 12:00:03"). Immune to clock differences between client and server, so `Retry-After` and `X-RateLimit-Reset` use it. | M3 |
| **Design-time factory** | `IDesignTimeDbContextFactory`: tells `dotnet ef` how to build the DbContext without starting the app. | M2 |
| **Dropped iterations** | Iterations k6 was scheduled to start but couldn't (it ran out of VUs). Non-zero means the run didn't apply the load it claims. | M4 |
| **EF Core migration** | A generated, committed C# file describing one schema change; `__EFMigrationsHistory` records which ones a database has run. | M2 |
| **EVALSHA** | Running a Redis script by its SHA-1 hash instead of resending its text. Redis replies `NOSCRIPT` if it has forgotten it. | M5 |
| **Fail fast** | Reporting an error immediately when success is already known to be impossible, rather than waiting for a timeout. | M5 |
| **Fail open / fail closed** | When a safety check can't run, *open* lets traffic through and *closed* blocks it. | M5 |
| **FakeTimeProvider** | A `TimeProvider` whose clock moves only when a test calls `Advance()`, so TTLs are tested without sleeping. | M2 |
| **Fallback limiter** | The per-instance in-memory bucket used for `payments` while Redis is unavailable. | M5 |
| **Fixed window** | A limiter that counts requests per calendar slot (e.g. per 10 s). It can be gamed at slot boundaries. | M4 |
| **GHCR** | GitHub Container Registry, where CI pushes Flux's images. | M7 |
| **Half-open** | The circuit-breaker state after the break duration, when exactly one trial call is let through to test the dependency. | M5 |
| **Hash (Redis)** | A Redis value holding field → value pairs, like a small dictionary. One rate-limit bucket = one hash (`tokens`, `ts`). | M5 |
| **Hash tag (Redis)** | The `{…}` part of a Redis key. In Redis Cluster, only that part decides which node owns the key. | M5 |
| **Histogram (metrics)** | A metric that counts observations into buckets (`le="0.001"`). Percentiles are estimated from the buckets, so the boundaries set the precision. | M3 |
| **k3s** | A lightweight Kubernetes distribution. It already runs on Megatron. | M7 |
| **Keep-alive** | Reusing one TCP connection for many HTTP requests. Matters for L4 balancing, which balances connections rather than requests. | M4 |
| **Key prefix** | The first 12 characters of an API key (`flux_devQuie`). Not secret: safe to log and display. | M2 |
| **L4 / L7 load balancing** | L4 balances TCP *connections*; L7 understands HTTP and balances *requests*. | M4 |
| **Launch profile** | A named way to `dotnet run` a project (port, environment variables), stored in `Properties/launchSettings.json`. It only affects `dotnet run`, never containers. | M1 |
| **Lazy refill** | Computing the tokens earned since the last request when a bucket is read, instead of adding them on a timer. | M3 |
| **Liveness / readiness** | *Liveness*: should this process be restarted? *Readiness*: should it receive traffic right now? | M1 |
| **Load shedding** | Deliberately refusing excess work early and cheaply under overload, so the system keeps serving what it accepts. | M6 |
| **Lost update** | Two read-modify-write sequences interleave, and one write silently overwrites the other's. | M5 |
| **Lua script (Redis)** | A small program Redis runs atomically. No other command runs while it executes. | M5 |
| **Megatron** | The home server (on the tailnet at 100.91.115.46). | M7 |
| **Merchant** | A tenant: a business calling the payments API. It has one plan and one or more API keys. | M2 |
| **MeterListener** | The .NET mechanism that receives measurements from `Meter` instruments. It is process-wide, which matters when one test process hosts several gateways. | M3 |
| **Microsoft.Testing.Platform (MTP)** | The test runner `dotnet test` uses in .NET 10 (replacing VSTest). xUnit v3 test projects are executables that include it. | M1 |
| **Middleware** | A step in ASP.NET Core's request pipeline. Each one can act on the request and then call the next, or stop and respond. | M1 |
| **Migration bundle** | A self-contained executable, built by `dotnet ef migrations bundle`, that applies migrations. M7 runs it as a k8s Job. | M7 |
| **Monotonic clock** | A clock that only moves forward and never jumps when the wall clock is corrected (`TimeProvider.GetTimestamp`). Used for elapsed time. | M3 |
| **Multi-stage build** | A Dockerfile with a heavy build stage and a slim runtime stage. Only the compiled output is copied into the final image. | M1 |
| **Multiplexer (Redis)** | StackExchange.Redis's single shared connection object, which pipelines every caller's commands over one socket. One per process. | M5 |
| **Negative caching** | Caching "this key does not exist", so floods of bad keys don't each reach the database. | M2 |
| **Noisy neighbour** | One tenant whose heavy traffic degrades service for other tenants. | M6 |
| **OpenTelemetry** | The vendor-neutral standard (and SDKs) for metrics, traces and logs. Flux uses its Prometheus exporter. | M3 |
| **p50 / p95 / p99** | Latency percentiles. p99 = 1 in 100 requests is slower than this value. | M4 |
| **Plan** | A named set of limits (free, pro), one limit per route group. | M2 |
| **Pooled DbContext factory** | `AddPooledDbContextFactory`: reuses DbContext instances, handing one out per unit of work. Used where a singleton needs the database occasionally. | M2 |
| **Problem Details** | RFC 9457: the standard JSON shape for HTTP API errors. | M2 |
| **Prometheus** | A monitoring system that scrapes `/metrics` endpoints and stores time series. | M3 |
| **Read-modify-write race** | Two actors read the same value, both modify it, and one write overwrites the other. | M5 |
| **RED method** | Monitor every service by Rate, Errors and Duration. | M3 |
| **Refill rate (`r`)** | Tokens added per second, which is the sustained request rate the plan allows. | M3 |
| **Reverse proxy** | A server that receives client requests and forwards them to backend servers, hiding the backends from the client. | M1 |
| **Route group** | The unit limits are applied to: `payments` or `refunds`. Several HTTP routes share one group. | M2 |
| **`Server-Timing`** | A W3C response header carrying server-side durations (`gw;dur=0.08, upstream;dur=6.0`). Shown by browser DevTools and readable by k6. | M3 |
| **Sliding window counter** | A fixed-window variant that blends the current count with a weighted share of the previous window, removing most of the boundary burst. | M4 |
| **Sticky sessions** | A load balancer sending one client's requests to the same server (by cookie or IP). | M4 |
| **Strategy pattern** | Hiding interchangeable algorithms behind one interface and choosing one by configuration (`IRateLimiter`). | M3 |
| **Tail latency** | The slow end of the latency distribution (p99, p99.9, max). What users of fan-out systems actually feel. | M6 |
| **Tailnet** | Your private Tailscale network. Only your devices can reach services exposed on it. | M7 |
| **Tenant** | One customer in a shared (multi-tenant) system. In Flux, a merchant. | M2 |
| **Testcontainers** | A library that starts real dependencies (PostgreSQL, Redis) in throwaway Docker containers for tests. | M2 |
| **Thundering herd** | Many waiting requests released at the same moment, overwhelming whatever they hit. E3's post-recovery burst. | M5 |
| **Time series** | One metric name plus one exact set of label values, stored as a sequence of samples. Every new label value creates a new series. | M3 |
| **Token bucket** | A limiter in which requests spend tokens and tokens refill at a steady rate, up to a cap. | M3 |
| **TTL (time to live)** | How long a cached entry is trusted before it must be looked up again. In Flux it is also the worst-case delay for a revocation. | M2 |
| **UUIDv7** | A UUID whose first bits are a timestamp, so new ids sort by creation time and index inserts stay at the end of the index (`Guid.CreateVersion7`). | M2 |
| **Valkey** | The open-source fork of Redis created after Redis's 2024 licence change. Protocol-compatible. | M5 |
| **Virtual user (VU)** | One concurrent k6 worker running the script's default function. | M4 |
| **Warm-up** | Requests or runs discarded or flagged because they pay one-off costs (JIT compilation, empty caches, new connections). | M6 |
| **`WWW-Authenticate`** | The header a `401` must include, telling the client which authentication scheme to use (`Bearer`). | M2 |
| **YARP** | "Yet Another Reverse Proxy": Microsoft's reverse-proxy library for ASP.NET Core. | M1 |
