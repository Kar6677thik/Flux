# Redis basics (for a rate limiter)

> Field Guide: §12.8 Distributed Cache, §16 Distributed systems (shared state), §20.3 Stateless application servers · Introduced in: M5
> Code: [`RateLimiting/RedisConnection.cs`](../../src/Flux.Gateway/RateLimiting/RedisConnection.cs), [`RateLimiting/RedisTokenBucket.cs`](../../src/Flux.Gateway/RateLimiting/RedisTokenBucket.cs), [`deploy/compose/docker-compose.yml`](../../deploy/compose/docker-compose.yml) (`redis`) · Decision: [ADR-0004](../adr/0004-redis-lua-server-clock.md)

## Problem
E1 measured the cost of per-instance state: 3 gateways let a merchant through at 2.99× its limit. The buckets have to live somewhere *every* gateway reads and writes, fast enough to sit on the hot path of every request.

## Context
Redis is an in-memory data-structure server: strings, hashes, lists, sets, sorted sets, streams. It executes commands on **one thread**, one at a time, so each individual command is atomic. It's a common home for rate limits, sessions, caches, leaderboards and locks.

## Solution
| Redis concept | How Flux uses it |
|---------------|------------------|
| **Hash** (`HSET key field value`) | One bucket = one hash: `flux:rl:{<merchantId>}:<routeGroup>` → `{ tokens: "9.000000000", ts: "1760…" }` |
| **Key expiry** (`PEXPIRE`) | Each bucket expires `ceil(B / r × 1000) + 1000` ms after its last use. By then it would be full anyway, so deleting it changes nothing and saves memory. |
| **Lua scripts** (`EVAL` / `EVALSHA`) | The whole read-refill-decide-write runs as one atomic script ([lua-atomicity](lua-atomicity.md)) |
| **`TIME`** | The script reads Redis's clock, so every gateway uses the same clock ([clock-skew](clock-skew.md)) |
| **Hash tags** (`{…}`) | In Redis Cluster only the part inside `{}` picks the shard, so a merchant's buckets stay together. With one node it just prepares for the future. |
| **No persistence** (`--save "" --appendonly no`) | Buckets are disposable. A restart resets limits to full; it loses no data that matters (spec §4). |

### The client: one multiplexer
StackExchange.Redis uses **one** `ConnectionMultiplexer` per process, shared by every request. It pipelines all callers' commands over a single socket, so you never create one per request (a classic mistake, which exhausts sockets). `RedisConnection` builds it once, at startup, **in the background**. The gateway must start and serve, degraded, even when Redis is down (`AbortOnConnectFail = false`).

### How the client behaves when Redis is gone (the E3 lesson)
| Setting | Default | Flux (resilient) | Why |
|---------|---------|------------------|-----|
| `AsyncTimeout` | 5,000 ms | 50 ms | With the default, a dead Redis adds 5 s to every request (E3: p50 5.5 s) |
| `BacklogPolicy` | queue while disconnected | `FailFast` | Queued commands each wait out the timeout, then replay as a burst on reconnect (E3: 138 false `429`s) |
| `AbortOnConnectFail` | true | false | Otherwise the gateway can't start while Redis is down |

## Alternatives
- **Memcached:** a cache only, with no scripting and no atomic read-modify-write beyond `incr`.
- **PostgreSQL** (`SELECT … FOR UPDATE`): correct, but a disk-backed database round trip with row locks on every request (ADR-0004).
- **Redis Cluster / Sentinel:** high availability. Out of scope (spec §3). We plan for Redis *failing* instead (ADR-0005).
- **Valkey:** the open-source fork of Redis after Redis's 2024 licence change. Protocol-compatible, and our code would work unchanged.

## Trade-offs
- **A network round trip per request.** P3 (M6) measures how much it adds.
- **A new critical dependency**, which is why M5 is "Redis + *resilience*".
- **Single-threaded means one slow command blocks everyone.** Our script is O(1) and microseconds long. Never run `KEYS *` on a production Redis.

## Implementation
`RedisTokenBucket.TryAcquireAsync` is one call: `ScriptEvaluateAsync(script, [key], [B, r])`, unpacking four integers. The client hashes the script, sends `EVALSHA` (just the 40-character SHA-1), and resends the full script by itself if Redis replies `NOSCRIPT` after a restart (`The_script_survives_redis_forgetting_it`).

## Testing
- Against a **real Redis 7.4** in Testcontainers ([`RedisTokenBucketTests`](../../tests/Flux.Gateway.IntegrationTests/RedisTokenBucketTests.cs)), never a mock.
- Redis's clock can't be faked, so tests that need time to pass **edit the stored `ts` field** ("pretend the last refill was 5 s ago") instead of sleeping. Tests that count admissions use a seeded `test-slow` plan (0.001 tokens/s), so the real time that passes *during* a test can't add a token. The first version of one test used the free plan and refilled 4 tokens in its 415 ms; that's the reason for the slow plan.

## Observability
`flux_ratelimiter_duration_seconds{algorithm="RedisTokenBucket"}` is the round trip as the gateway sees it. With `redis-cli`: `INFO stats` (ops/sec), `SLOWLOG GET`, `--latency`.

## Related
[lua-atomicity](lua-atomicity.md) · [clock-skew](clock-skew.md) · [timeouts-and-circuit-breakers](timeouts-and-circuit-breakers.md) · [load-balancing](load-balancing.md) · ADR-0004, ADR-0010
