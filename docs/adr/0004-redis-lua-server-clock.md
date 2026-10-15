# ADR-0004: Shared buckets in Redis, updated by one Lua script using Redis's clock

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M5)

## Context
With 3 gateway instances, in-memory buckets give a merchant 3× their limit (experiment E1). The buckets must be shared. Sharing brings two new problems:

1. **The read-modify-write race.** Instances A and B both read "1 token left", both allow their request, and both write 0. One extra request gets through. Under load this happens constantly.
2. **Clock skew.** If each instance computes the refill from *its own* clock, a gateway whose clock runs 200 ms fast hands out extra tokens.

## Decision
- Store each bucket as a Redis hash: `flux:rl:{merchantId}:routeGroup` → `{tokens, ts}`.
- Do the whole refill → check → spend → write → expire sequence in **one Lua script** (`EVALSHA`). Redis runs a script without interleaving any other command, so the sequence is atomic.
- Inside the script, read the time with `redis.call('TIME')`, so there is **one clock**: Redis's own.
- Return integers only, because Redis truncates Lua floats.

## Alternatives considered
| Option | Why not |
|--------|---------|
| `WATCH`/`MULTI`/`EXEC` (optimistic transaction) | Correct, but under contention it retries constantly and needs several round-trips. That is exactly the hot-merchant case we care about. |
| `INCR` + `EXPIRE` (fixed window in Redis) | Atomic and simple, but reintroduces the boundary burst (ADR-0002). |
| Gateway sends its own timestamp | Brings back clock skew between instances. |
| Redis 7 Functions instead of `EVAL` scripts | Same atomicity, and the functions persist in Redis. A good evolution, but `EVAL`/`EVALSHA` is what most material teaches and is enough here. |
| PostgreSQL row with `SELECT … FOR UPDATE` | Correct, but adds a database round-trip and lock contention to every request. Redis is in-memory and single-threaded for commands. |
| Sync buckets between instances (gossip) | Eventually consistent, so it over-admits during the sync delay, and it is far more complex. |

## Consequences
- Each rate-limit check costs one network round-trip to Redis, which adds latency. P3 measures how much.
- Redis becomes a dependency on the hot path, so we must decide what happens when it fails (ADR-0005).
- Redis is not a source of truth. If it restarts empty, buckets just start full again, so no persistence is configured.

## Revisit when
Redis round-trip time dominates gateway overhead. The next step would be *local batching* (each instance reserves a small batch of tokens) or a hybrid local + global limiter.
