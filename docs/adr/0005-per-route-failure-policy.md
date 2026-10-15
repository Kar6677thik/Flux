# ADR-0005: Per-route failure policy when Redis is unavailable

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M5)

## Context
Once buckets live in Redis (ADR-0004), a Redis outage forces a choice on every request. Either we **fail open** (let traffic through without the shared limit) or we **fail closed** (reject it). The project guide asks us to decide *per route* and write down why. There is a hidden third problem too: a slow failure. The client library's default 5 s timeout makes every request hang, which is worse than either choice.

## Decision

| Route group | Policy | Behaviour |
|-------------|--------|-----------|
| `payments` | **Fail open, with a local fallback** | Each instance uses its own in-memory token bucket, sized at the plan limit ÷ `AssumedInstances` (default 3). Responses carry `X-RateLimit-Degraded: true`. |
| `refunds` | **Fail closed** | `503 rate_limiter_unavailable` with `Retry-After: 5` |

**Common to both:**
- Each Redis call has a 50 ms timeout.
- A circuit breaker opens when ≥ 50% of at least 20 calls fail within 10 s. It stays open for 5 s, then lets a trial call through.
- While the breaker is open, the policy applies immediately, without waiting for a timeout.
- Readiness never depends on Redis.

## Why payments fail open
- A payment request is money **coming in**, usually with the merchant's customer waiting at checkout. Rejecting it costs revenue and trust, which is worse than briefly allowing slightly more traffic.
- "Open" is not "unlimited". The local fallback still caps each instance, so the worst case is roughly the plan limit (÷3 × 3 instances). Abuse protection degrades a little; it doesn't vanish.

## Why refunds fail closed
- A refund is money **going out**. Abusive or fraudulent refund floods are more damaging than delayed refunds.
- Refunds are rarely urgent. A merchant retrying after 5 s is an acceptable cost.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Fail open everywhere, with no limit | A Redis outage would become an open door at exactly the moment attackers love: something is already broken. |
| Fail closed everywhere | A Redis outage becomes a total payments outage. The rate limiter would be a single point of failure for revenue. |
| Fallback limit = full plan limit per instance | Simpler, but allows 3× the limit while degraded. Dividing by the expected instance count stays closer to the real quota. |
| Return `429` instead of `503` on fail-closed | `429` tells the client *it* sent too much. Here the problem is ours, so `503` is the honest status. |

## Consequences
- During an outage, payments limits are approximate. Our metrics and the `X-RateLimit-Degraded` header make this visible.
- `AssumedInstances` must be kept in step with the actual replica count. On k3s it is set from the same value as `replicas`.
- Experiment P4 must show both policies working, with bounded latency.

## Revisit when
The number of instances changes dynamically (autoscaling). Then the fallback divisor should come from live membership, not from configuration.
