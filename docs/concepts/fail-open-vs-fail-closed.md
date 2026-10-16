# Fail open vs. fail closed

> Field Guide: §17.7 Fallback, §17.8 Graceful Degradation, §17.24 Fail-Safe Defaults, §25.3 Secure by Default · Introduced in: M5
> Code: [`RateLimiting/ResilientRateLimiter.cs`](../../src/Flux.Gateway/RateLimiting/ResilientRateLimiter.cs) (`DegradeAsync`), [`RateLimiting/RateLimitMiddleware.cs`](../../src/Flux.Gateway/RateLimiting/RateLimitMiddleware.cs) · Decision: [ADR-0005](../adr/0005-per-route-failure-policy.md) · Experiment: [P4](../reports/P4-redis-outage-resilient.md)

## Problem
When a *safety check* can't run (here, "is this merchant over its limit?" while Redis is down), the system still has to answer the request. Either answer is a choice with a cost:
- **fail open:** let it through, and lose protection;
- **fail closed:** reject it, and lose availability.

There's no option that avoids both, so the choice must be deliberate. ADR-0005 makes it **per route**, because the cost differs per route.

## Context
The same choice appears everywhere:
- **Fail closed:** a firewall whose rules fail to load blocks traffic; an auth service that's down rejects logins; a smoke-door magnet drops the door shut on power loss.
- **Fail open:** an electronic lock on a fire exit unlocks on power loss; a feature-flag service that's down serves defaults; a CDN serves stale content when the origin is down.

The rule of thumb: fail closed when the harm of letting the wrong thing through outweighs the harm of blocking the right thing.

## Solution
| Route group | Policy | Why | What the client saw in P4 |
|-------------|--------|-----|---------------------------|
| `payments` | **Fail open, bounded** | Money coming in, customer at checkout: rejecting it loses revenue and trust | `200`, `X-RateLimit-Degraded: true`, normal latency (p99 ~13 ms) |
| `refunds` | **Fail closed** | Money going out, an attractive abuse target, rarely urgent | `503 rate_limiter_unavailable`, `Retry-After: 5`, p50 ~1.1 ms |

### "Open" is not "unlimited"
While Redis is down, each gateway enforces a **local share** of the limit: refill `r ÷ N` and burst `ceil(B ÷ N)` (minimum 1), where `N = AssumedInstances` (default 3, kept equal to the replica count). Three gateways each allowing a third adds up to roughly the plan, even with no shared state. That's the "divide by N" idea from the [load-balancing note](load-balancing.md), acceptable *only* as a degraded mode, because it's wrong whenever traffic is uneven or N changes. Tested: a free merchant (B = 10) gets exactly 4 of 6 requests while degraded (`With_redis_down_payments_fail_open_to_a_local_share_of_the_limit`).

### `503`, not `429`, for fail closed
`429` means "*you* sent too much". During an outage the client did nothing wrong, and the limiter is what's broken. `503` plus `Retry-After` says so honestly, and well-behaved clients back off and retry.

### Make degraded mode visible
- **Clients** see `X-RateLimit-Degraded: true`.
- **Operators** see `flux_ratelimiter_fallback_total{route_group, policy}`, the breaker gauge, and breaker-transition logs.

A silent fail-open is how "temporary" degraded modes become permanent without anyone noticing.

## Alternatives
| Option | Why not (ADR-0005) |
|--------|---------------------|
| Fail open everywhere, unlimited | An outage becomes an open door at exactly the moment attackers love: something is already broken |
| Fail closed everywhere | A Redis outage becomes a total payments outage. The limiter is then a single point of failure for revenue |
| Fallback at the full plan limit per instance | Simpler, but 3× the limit while degraded |
| Fall back to "last known state" | We don't have one: buckets live only in Redis |

## Trade-offs
- **During an outage, payments limits are approximate.** The divisor assumes even balancing.
- **`AssumedInstances` must track the replica count.** On k3s (M7) both should come from one value. If they drift, the degraded limit drifts with them (ADR-0005's "revisit when": autoscaling).
- **Refund callers must handle `503`.** That's normal HTTP hygiene, and `Retry-After` tells them when.

## Implementation
```csharp
var policy = _options.FailurePolicy[key.RouteGroup];             // from config, per route group
_metrics.LimiterFallback(key.RouteGroup, policy);
if (policy == FailurePolicy.FailClosed)
    throw new RateLimiterUnavailableException(...);              // middleware → 503 + Retry-After: 5
var share = new RouteLimit(r / N, max(1, ceil(B / N)));
return (await _fallback.TryAcquireAsync(key, share, ct)) with { Source = DecisionSource.Fallback };
```
The fallback is M3's `InMemoryTokenBucket`, reused unchanged. That's one payoff of solving the bucket maths before the distribution problem.

## Testing
- Integration ([`RedisResilienceTests`](../../tests/Flux.Gateway.IntegrationTests/RedisResilienceTests.cs)), with a dead Redis port and with a real Redis container stopped mid-test: payments degrade with the right share, refunds get `503` with `Retry-After: 5`, and readiness stays `200`.
- End to end: P4, both `stop` and `pause`, five runs.

## Observability
`flux_ratelimiter_fallback_total{policy="fail_open"}` and `{policy="fail_closed"}`, plus `X-RateLimit-Degraded` in client-side logs and k6.

## Related
[timeouts-and-circuit-breakers](timeouts-and-circuit-breakers.md) · [token-bucket](token-bucket.md) · [load-balancing](load-balancing.md) · ADR-0005
