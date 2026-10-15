# ADR-0002: Token bucket as the production rate-limiting algorithm

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M3, made distributed in M5)

## Context
Merchants have plans such as "10 requests/s on payments". We need an algorithm that:
1. enforces that average rate;
2. tolerates small, natural bursts (a checkout page firing a few calls at once);
3. has **no window boundary** where a client can double its quota (the Redis 2026 post lists this as a mistake teams still make);
4. is cheap: little state per merchant and route, and one atomic step in Redis.

## Decision
Use a **token bucket** with lazy refill:
- Each `(merchant, routeGroup)` bucket stores two numbers: `tokens` and `lastRefillTime`.
- The plan supplies the refill rate `r` and the capacity `B`.

The exact formulas are in spec §9.2.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Fixed window counter | Simplest of all, but allows 2× the quota across a boundary. We build it anyway, to *demonstrate* that flaw (experiment E2). |
| Sliding window log | Exact, but stores one timestamp per request: memory grows with traffic. Too expensive at Razorpay's scale, and pointless for us. |
| Sliding window counter | A good approximation that needs only two counters. It doesn't let you configure bursts separately from the rate, and the maths is slightly harder to explain. Listed as a stretch goal for comparison. |
| Leaky bucket (queue) | Smooths traffic by *delaying* requests. An API gateway should reject with a `429` rather than hold connections open. |
| ASP.NET Core's `System.Threading.RateLimiting` | Has a token bucket, but it is in-process only. We want to understand the algorithm and make it distributed ourselves. A stretch goal compares the two. |

## Consequences
- Burst capacity (`B`) and sustained rate (`r`) are separate settings, so plans can grow bursts without code changes.
- The `Remaining`/`Reset` headers are fractional by nature. We round them (floor and ceil) as defined in spec §7.3.
- Lazy refill depends on a clock. That makes clock choice an explicit design question: `TimeProvider` in memory, Redis `TIME` in Redis (ADR-0004).

## Revisit when
A plan needs "N requests per day" quotas. Those are better served by a separate counter with a long window, alongside the token bucket.
