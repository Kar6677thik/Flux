# Atomicity with Lua scripts (and the read-modify-write race)

> Field Guide: §11 Transactions and concurrency, §14 Concurrency (race conditions), §16 Distributed systems · Introduced in: M5
> Code: [`RateLimiting/token_bucket.lua`](../../src/Flux.Gateway/RateLimiting/token_bucket.lua), [`RateLimiting/RedisTokenBucket.cs`](../../src/Flux.Gateway/RateLimiting/RedisTokenBucket.cs) · Decision: [ADR-0004](../adr/0004-redis-lua-server-clock.md)

## Problem
A token bucket is read-modify-write: *read* tokens, *compute* the refill, *decide*, *write* the new count. If two gateways do that against shared state as separate steps:
```
gateway A: HGET → tokens = 1                         gateway B: HGET → tokens = 1
gateway A: 1 ≥ 1 → allow, HSET tokens = 0            gateway B: 1 ≥ 1 → allow, HSET tokens = 0
→ two requests admitted on one token. Under load this happens constantly (the lost update).
```
M3 solved this inside one process with a lock per bucket. Across processes, a C# `lock` means nothing.

## Context
This is the same race behind double-spending, overselling the last concert ticket, and lost updates in a database. The standard fixes: make the whole sequence **atomic** (one indivisible step), use **optimistic concurrency** (detect conflicts and retry), or take a **lock**.

## Solution
Redis runs a Lua script **as one command**. No other client's command is executed until the script finishes. So the whole sequence below can't interleave with another gateway's:

```lua
local time = redis.call('TIME')                         -- Redis's clock (clock-skew note)
local state = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
-- refill: tokens = min(B, tokens + elapsed × r)
-- decide: if tokens ≥ 1 then tokens -= 1, allowed = 1
redis.call('HSET', KEYS[1], 'tokens', …, 'ts', …)
redis.call('PEXPIRE', KEYS[1], ceil(B / r × 1000) + 1000)
return { allowed, floor(tokens), retry_after_ms, reset_after_ms }
```
And it's one network round trip instead of four.

### Lua-in-Redis gotchas, all handled in the script
- **Replies are truncated to integers.** Return `0.97` and the client receives `0`. So the script returns `allowed` (0/1), whole `remaining`, and times in **milliseconds**.
- **Numbers stored as strings.** `HSET` stores what it's given; `string.format('%.9f', tokens)` keeps nine decimals of precision, the same rounding as the in-memory bucket.
- **Keys must be passed in `KEYS[]`**, not built inside the script. That's how Redis Cluster knows which shard a script touches.
- **Scripts must be short.** While one runs, Redis does nothing else. Ours is O(1), microseconds.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| `WATCH` / `MULTI` / `EXEC` (optimistic) | Correct, but under contention, the hot-merchant case, it aborts and retries repeatedly, each time with several round trips |
| `INCR` + `EXPIRE` | Atomic, but it's a fixed window: the boundary burst comes back (E2) |
| A distributed lock (`SET NX PX`) around read/write | Two extra round trips, lock expiry edge cases, and a lock held across the network |
| Redis Functions (Redis 7+) | The same atomicity, loaded once and persisted server-side. A good next step; `EVAL` is what most material teaches |
| Gateway-side "reserve a batch of N tokens" | Fewer round trips, at the cost of some precision. ADR-0004's "revisit when" |

## Trade-offs
- **Redis is a single point of contention** for a very hot key. That's fine at our scale; at Razorpay's, hot merchants get sharded or locally batched.
- **Business logic in Lua, inside the data store.** It needs its own tests (it has them), and a deploy updates it implicitly: `EVALSHA` keys on the script's hash, so the new text simply loads under a new SHA.

## Implementation
- The script is a real `.lua` file, embedded in the assembly, so it reads and diffs as Lua rather than as a C# string.
- `ScriptEvaluateAsync(string)` lets the client manage `EVALSHA` and `NOSCRIPT` recovery itself.

## Testing
`Five_hundred_parallel_requests_from_two_gateways_allow_exactly_the_burst` uses two separate connections (two "gateways") firing 500 concurrent requests at a 100-token bucket, and asserts **exactly 100** allowed (spec §14). With separate HGET/HSET calls this would over-admit. With in-memory buckets it would allow 200. `Two_gateways_share_one_bucket_which_is_the_fix_for_E1` checks the same through two full gateway hosts.

## Observability
Over-admission shows up as allowed counts above `B + r·T`. P1 (M6) checks exactly that bound across 3 gateways.

## Related
[redis-basics](redis-basics.md) · [clock-skew](clock-skew.md) · [token-bucket](token-bucket.md) · ADR-0004
