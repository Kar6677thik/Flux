# Clock skew

> Field Guide: §16 Fundamental distributed-systems problems (time, clocks, ordering) · Introduced in: M5
> Code: [`RateLimiting/token_bucket.lua`](../../src/Flux.Gateway/RateLimiting/token_bucket.lua) (`redis.call('TIME')`) · Decision: [ADR-0004](../adr/0004-redis-lua-server-clock.md)

## Problem
A token bucket refills by *elapsed time*: `tokens += (now − last) × r`. With a shared bucket, *whose* `now`? If each gateway passed its own clock reading to Redis, and gateway A's clock ran 300 ms ahead of gateway B's:
```
B writes ts = 10.000 (B's clock)
A reads it at "10.300" on A's clock, though only ~1 ms has really passed
→ A grants 0.3 s × r extra tokens: r = 100/s means 30 free tokens
→ then B sees ts in its future: elapsed is negative, and tokens may *vanish* without a max(0, …) guard
```
Machines' clocks always disagree a little (NTP keeps them within milliseconds when healthy, and seconds or worse when it isn't), and the error goes straight into the limit.

## Context
Distributed systems hit this everywhere: ordering events by timestamp, lease and lock expiry, "last write wins", JWT `exp`/`nbf` checks (with their deliberate leeway), and log correlation. The general lesson is **don't compare timestamps taken on different machines** unless you can bound the skew.

## Solution
**Use one clock.** The Lua script reads Redis's own clock with `redis.call('TIME')` (seconds plus microseconds) and stores `ts` in microseconds. Every gateway's refill is computed by the same machine's clock, so skew between gateways can't exist for this purpose.

Two smaller defences:
- **`math.max(0, now − last)`** in the script. If Redis's clock ever steps backwards (an NTP correction, a failover to a replica with a different clock), elapsed time is treated as 0, not negative.
- **Delta seconds in headers** (`Retry-After: 1`, not a timestamp): the *client's* clock never has to agree with ours ([rate-limit-headers](rate-limit-headers.md)).

In memory (M3), the bucket uses a **monotonic** clock (`TimeProvider.GetTimestamp`), which never jumps at all. Redis's `TIME` is wall-clock time, hence the guard above.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| Gateway sends its own timestamp | Brings skew straight back. Rejected in ADR-0004 |
| Logical clocks (Lamport, vector clocks) | Order events without wall time, but a rate is "tokens per *second*": we need real elapsed time |
| Hybrid logical clocks / TrueTime (Spanner) | Bounded-uncertainty time across a fleet: real, powerful, and far beyond what a limiter needs |
| Tolerate skew, keep NTP tight | Works until NTP breaks, and you then debug "limits feel wrong" without a clue |

## Trade-offs
- **Redis's clock becomes the authority.** If *it* jumps forward (badly configured NTP on the Redis host), every bucket refills early. The guard only handles backward steps.
- **Failover moves the clock.** A replica promoted to primary brings its own clock; a backward step is clamped to zero refill, a forward one over-refills briefly. Out of scope (single Redis), but the guard is cheap and already there.

## Implementation
`now_us = TIME[1] × 1,000,000 + TIME[2]`: seconds and microseconds, so refill isn't quantised to whole milliseconds. That fits comfortably in Lua's 53-bit number precision (~1.7 × 10¹⁵ µs today).

## Testing
Redis's clock can't be faked, so tests shift the stored `ts` back by a known amount (`Tokens_refill_by_elapsed_redis_time`: 5 s back → exactly 5 tokens at 1/s). A clock-skew *demonstration* would need two gateways passing skewed timestamps, which is exactly the design ADR-0004 rejected, so it exists only as exercise 3 in journal 05.

## Observability
Hard to see directly. The signature is "allowed counts consistently above `B + r·T` across instances" (P1) without any race to blame.

## Related
[lua-atomicity](lua-atomicity.md) · [redis-basics](redis-basics.md) · [testing-time](testing-time.md) · ADR-0004
