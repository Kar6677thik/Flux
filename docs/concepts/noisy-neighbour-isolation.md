# Noisy-neighbour isolation

> Field Guide: §20.15 Noisy Neighbor Isolation, §20.16 Per-Tenant Quotas, §20.18 Admission Control, §17.9 Load Shedding · Introduced in: M6 (built across M2–M5)
> Code: the per-merchant bucket key in [`RateLimitMiddleware`](../../src/Flux.Gateway/RateLimiting/RateLimitMiddleware.cs) · Experiment: [P2](../reports/P2-noisy-neighbour.md)

## Problem
In a shared system, one tenant's burst consumes capacity everyone depends on: backend threads, database connections, CPU. One global limit doesn't help either. The Redis 2026 throttling post calls this out: when everyone shares a limit, the noisiest client takes it all and quiet clients starve.

## Context
Every multi-tenant platform faces it: SaaS APIs, cloud providers (a "noisy neighbour" VM on the same host), Kubernetes pods without resource limits. Razorpay's merchants range from tiny shops to huge enterprises on the same gateway.

## Solution
**Give each tenant its own budget, and enforce it as early as possible.**
- **Per tenant:** the bucket key is `(merchantId, routeGroup)`. A merchant can only ever spend its own tokens, and P2 measured noisy-corp getting exactly 609, at any rate.
- **Early:** the check happens at the gateway, *before* forwarding. A refused request costs the gateway a cache lookup and one Redis call, and costs the backend nothing. In P2 the payments backend's CPU stayed at **2%** while 1,000 extra requests per second arrived.
- **Per merchant, not per key:** extra API keys don't buy extra budget (spec §5).

### What it does *not* isolate
The *gateway itself* still does work for every refused request (auth from cache, the Redis check, writing the `429`). P2 measured the gateway's CPU rising **about 10×** (6% → 58% of a core) during the flood. Rate limiting protects what's *behind* it. Protecting the limiter itself needs layers in front:
- per-IP or per-connection limits at the edge (a stretch goal);
- **load shedding** (§17.9): refuse cheaply under overload, before doing per-request work;
- enough gateway replicas (headroom, §20.22).

## Alternatives
| Approach | Trade-off |
|----------|-----------|
| One global limit | Simple, and exactly the failure mode described above |
| Per-tenant queues / weighted fair queuing (§20.17) | Shares capacity fairly by *delaying*, not refusing. Good for batch work; a payment API should answer now |
| Cell-based architecture (§20.14) | Big tenants get their own cells. Strong isolation at a big operational cost |
| Concurrency limits per tenant | Cap in-flight requests instead of rate. It protects against slow requests that a rate limit can't see |

## Trade-offs
- **Quotas are a product decision** (plans), not only an engineering one. Getting `r` and `B` wrong either throttles real customers or doesn't protect anything.
- **Isolation is only as good as the shared parts.** P2 ran on one Redis and one gateway. A flood large enough to saturate the gateway's CPU would hurt everyone. That's the next layer's job.

## Implementation
There's nothing new in M6. Isolation is the *sum* of earlier milestones: tenancy (M2), the bucket key (M3), the shared bucket (M5), and refusing before forwarding (M3).

## Testing
- `Limits_are_per_merchant_so_a_second_key_buys_nothing` and `A_limited_request_is_never_forwarded` (integration).
- **P2 end to end:** quiet-shop's p95 showed no increase under a 100× flood (three paired runs, plus a control showing that the apparent *improvement* was laptop power management).

## Observability
No merchant label in metrics (cardinality). A noisy merchant shows in aggregate as a rising `outcome="limited"` rate, and is identified from logs (`merchantId` on every line) or from k6 tags in experiments.

## Related
[multi-tenancy](multi-tenancy.md) · [token-bucket](token-bucket.md) · [load-balancing](load-balancing.md) · [benchmarking-methodology](benchmarking-methodology.md)
