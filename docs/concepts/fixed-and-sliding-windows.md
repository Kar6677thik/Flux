# Fixed and sliding windows

> Field Guide: §17.11 Rate Limiting, §17.13 Token Bucket (for contrast) · Introduced in: M4
> Code: [`RateLimiting/InMemoryFixedWindow.cs`](../../src/Flux.Gateway/RateLimiting/InMemoryFixedWindow.cs) · Experiment: [E2](../reports/E2-fixed-window-boundary.md) · Decision: [ADR-0002](../adr/0002-token-bucket.md)

## Problem
"100 requests per 10 seconds" sounds simple to implement: count requests, reset the counter every 10 seconds. The Redis 2026 throttling post lists exactly this as a mistake teams still make, because the reset is predictable and a client can aim at it.

## Context
Fixed windows are everywhere because they're the first thing anyone writes: `INCR key; EXPIRE key 10` in Redis, or "requests this minute" in a SQL table. Daily and monthly *quotas* (billing, "1,000 calls per day") are legitimately fixed windows, since the business means the calendar day. For *protecting a backend* from bursts, they're the wrong tool.

## Solution (and its flaw)
```
count[key, floor(now / W)]++   allow while count ≤ r × W

window k             │ window k+1
   ………… 100 requests │ 100 requests …………
            9.85 s  10.0 s  10.05 s
                     ↑ counter resets to 0
→ 200 allowed in ~0.2 s, where the plan says 10/s (E2 measured 200 in 232–237 ms)
```
Each window *individually* respects its quota. The guarantee people assume ("never more than 100 in any 10 s span") is false: any span that straddles a boundary can hold up to **2 × quota**.

### The family of fixes
| Algorithm | Idea | Memory per key | Boundary burst? |
|-----------|------|----------------|-----------------|
| Fixed window | One counter per calendar slot | 1 counter | **Yes, up to 2×** |
| Sliding window log | Keep a timestamp per request; count the last W seconds | One entry per request, so it grows with traffic | No, exact |
| Sliding window counter | `current + previous × (fraction of the previous window still inside the last W)` | 2 counters | Mostly no (an approximation that assumes even spread in the previous window) |
| Token bucket (Flux) | Tokens accrue continuously, capped at B | 2 numbers | No: there's no boundary |

Worked example of the sliding counter: at 10.05 s, with W = 10 s, the previous window had 100 and the current one 0. The estimate is `0 + 100 × 0.995 ≈ 99.5`, so the 100 new requests are refused. That's why it's the usual fix when you want to keep "per window" semantics.

## Alternatives
Covered in the table above. For Flux the token bucket wins ([ADR-0002](../adr/0002-token-bucket.md)): no boundary, two numbers of state, and a natural fit for "burst of B, average of r".

## Trade-offs
- **Fixed windows are easy to explain and to bill.** "You used 812 of your 1,000 calls today" is a fixed window, and that's fine for quotas.
- **Predictability helps attackers.** Windows aligned to the epoch, with `X-RateLimit-Reset` announcing the next boundary, tell a client exactly when to fire.
- **Randomising window starts per key** blunts the aiming (the attacker can no longer predict the boundary), but each key still has a boundary.

## Implementation
`InMemoryFixedWindow` stores just `(windowIndex, count)` per key and resets the count when the index changes. It uses **wall-clock** time (`GetUtcNow`), unlike the bucket's monotonic clock, because a fixed window *is* a calendar concept. That's exactly what makes the boundary shared and predictable. Its `Limit` header is the per-window quota (`r × W = 100`), which is why `RateLimitDecision` carries its own `Limit` since M4. The class comment says, in capitals, never to select it for real traffic.

## Testing
[`InMemoryFixedWindowTests`](../../tests/Flux.Gateway.UnitTests/InMemoryFixedWindowTests.cs) demonstrates the flaw at exact instants. Starting at 9.9 s into a window: 100 allowed, the 101st refused, `Advance(200 ms)`, 100 more allowed (`The_boundary_burst_allows_twice_the_quota_in_200_ms`). The same sequence against `InMemoryTokenBucket` allows 12. Then E2 reproduces it over real HTTP with real timing.

## Observability
In production, the signature is a sawtooth: `limited` drops to zero right after each boundary, while `allowed` spikes at boundary instants. With a token bucket the `allowed` rate stays flat at r.

## Related
[token-bucket](token-bucket.md) · [rate-limit-headers](rate-limit-headers.md) · [load-testing-with-k6](load-testing-with-k6.md) · E2 report
