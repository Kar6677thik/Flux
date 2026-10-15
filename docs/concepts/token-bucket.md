# The token bucket

> Field Guide: §17.11 Rate Limiting, §17.13 Token Bucket, §17.14 Leaky Bucket, §20.16 Per-Tenant Quotas, §14 (per-bucket locking), §3 Strategy pattern · Introduced in: M3
> Code: [`RateLimiting/InMemoryTokenBucket.cs`](../../src/Flux.Gateway/RateLimiting/InMemoryTokenBucket.cs), [`RateLimiting/RateLimitMiddleware.cs`](../../src/Flux.Gateway/RateLimiting/RateLimitMiddleware.cs), [`RateLimiting/IRateLimiter.cs`](../../src/Flux.Gateway/RateLimiting/IRateLimiter.cs) · Decision: [ADR-0002](../adr/0002-token-bucket.md)

## Problem
Up to M2, an authenticated merchant could send unlimited traffic. One merchant's retry storm would land straight on the backends and slow everyone down. A plan saying "free: 10 req/s on payments" needs to be enforced, but real clients are bursty: a checkout page fires three calls at once, then nothing for seconds.

## Context
Every public API limits rates: Stripe, GitHub, Razorpay, AWS (whose API throttling is documented as a token bucket). Network equipment has done it for decades, for traffic shaping. In Flux it's step 5 of the request lifecycle, right after auth.

## Solution
Picture a bucket that holds at most **B** tokens and gains **r** tokens per second. Each request takes one. No token, no entry.

```
capacity B = 10, refill r = 10/s, starts full

t = 0.00  25 requests  → 10 allowed (tokens 10 → 0), 15 × 429
t = 0.35  1 request    → bucket gained 0.35 × 10 = 3.5 → allowed, 2.5 left → Remaining: 2
```
- **Bursts up to B are fine;** the **long-run average** can't exceed r. Over T seconds a client gets at most `B + r·T` requests. That bound is exactly what experiment P1 checks with three gateways.
- **No window boundary.** A fixed window resets a counter on the clock (M4's E2 exploits that), but a bucket has no edges.
- **Two numbers per bucket:** `tokens` and `lastRefill`.

### Lazy refill: no timers
Nothing adds tokens in the background. When a request arrives, the bucket works out what has accrued since it was last looked at:
```
tokens  = min(B, tokens + (now − lastRefill) × r)
allowed = tokens ≥ 1  → tokens −= 1
```
A million idle buckets cost nothing, and there's no timer to drift or race.

### The numbers that become headers
| Value | Formula | Example (B=10, r=10, 0.3 tokens left) |
|-------|---------|------|
| `Remaining` | `floor(tokens)` | 0 |
| `RetryAfter` (only when refused) | `(1 − tokens) / r` | 70 ms → header `1` |
| `ResetAfter` (until completely full) | `(B − tokens) / r` | 970 ms → header `1` |

## Alternatives
| Algorithm | Behaviour | Why not (here) |
|-----------|-----------|----------------|
| Fixed window | `count[floor(now/W)]++` | 2× burst at boundaries. Built in M4 *to demonstrate* that. |
| Sliding window log | One timestamp per request | Exact, but memory grows with traffic |
| Sliding window counter | Weighted average of two windows | Good approximation, cheap. A stretch goal |
| Leaky bucket (§17.14) | A queue drained at a constant rate | *Smooths* traffic by delaying it. A gateway should answer now, not queue |
| GCRA | A token bucket expressed as one timestamp ("theoretical arrival time") | Equivalent and even leaner. Worth reading about; less intuitive to learn first |
| ASP.NET Core `System.Threading.RateLimiting` | Built-in `TokenBucketRateLimiter` | Per-process only, refills on a timer, not easy to make distributed. A comparison is a stretch goal |

## Trade-offs
- **Per instance.** This `InMemoryTokenBucket` lives in one process. Three gateways behind a round-robin balancer give a merchant three buckets, so 3× the limit. That's E1 in M4, fixed by Redis in M5. The in-memory version is still useful: it's M5's fallback, and the unit tests pin down the maths exactly.
- **Tokens are spent at admission**, even if the backend then fails (spec §5). We protect the backend, and it did receive the request.
- **Rejected requests spend nothing.** A `401` has no merchant, so no bucket. A `429` doesn't drain the bucket further.

## Implementation
- **The Strategy seam.** `RateLimitMiddleware` depends on `IRateLimiter`, chosen by `RateLimiting:Algorithm`. M4 and M5 add implementations without touching the middleware.
- **One lock per bucket**, not one for the whole limiter (C# 13's `System.Threading.Lock`), so merchants never wait on each other. Without a lock, two threads could both read "1 token left" and both spend it, a *read-modify-write race*. `Concurrent_requests_never_spend_the_same_token_twice` fires 1,000 parallel requests at a 100-token bucket and expects exactly 100 allowed.
- **Monotonic time.** `TimeProvider.GetTimestamp()` never jumps backwards when NTP corrects the wall clock, so a clock correction can't mint or destroy tokens.
- **Floating-point care, which M3's tests caught:**
  - `tokens` is rounded to 1e-9 after each refill, so `0.1 s × 10/s` can't come out as `0.9999999` tokens and refuse a request that should pass.
  - Durations are rounded **up** to the next 100 ns tick. `(1 − 0.3) / 10` is `0.06999…` in binary, and `TimeSpan.FromSeconds` truncates it, so a client that waited exactly `RetryAfter` would arrive one tick early and be refused again. `Waiting_exactly_retry_after_is_enough_from_a_fractional_bucket` failed before this fix.
- **Eviction.** Buckets idle for 10 minutes are swept (at most once a minute, by whichever request notices). An idle bucket has refilled anyway, so dropping it changes nothing, as long as `B / r` is under 10 minutes, which holds for every plan.

## Testing
- **Unit, at exact instants** ([`InMemoryTokenBucketTests`](../../tests/Flux.Gateway.UnitTests/InMemoryTokenBucketTests.cs)): burst = B, journal 00's exercise (25 at once, then 1 at 350 ms), exact `RetryAfter`/`ResetAfter`, the refill cap, the long-run average (109 of 200 at 20/s offered), a fractional rate (0.5/s), independent keys, concurrency, eviction.
- **Integration** ([`RateLimitContractTests`](../../tests/Flux.Gateway.IntegrationTests/RateLimitContractTests.cs)): through the real gateway. The 11th request gets `429`; a limited request **never reaches the upstream** (the echo server counts requests); GET and POST share a bucket; a second key of the same merchant is already empty; another merchant on the same plan isn't affected.

## Observability
- `flux_requests_total{outcome="limited"}` by route group. A rising share of `limited` against `allowed` is the first signal of a noisy merchant (find out *which* one from logs, not metrics: [cardinality](metrics-and-cardinality.md)).
- `flux_ratelimiter_duration_seconds{algorithm}`: in memory this should sit in the lowest bucket. In M5 it becomes the Redis round trip.

## Related
[rate-limit-headers](rate-limit-headers.md) · [testing-time](testing-time.md) · [multi-tenancy](multi-tenancy.md) · later: fixed-and-sliding-windows (M4), lua-atomicity (M5) · ADR-0002
