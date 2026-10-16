# Load balancing (L4 vs. L7) and shared state

> Field Guide: §20.2 Horizontal Scaling, §20.3 Stateless Application Servers, §20.4 Load Balancing, §15 Networking, §16 Distributed systems (shared state) · Introduced in: M4
> Code: [`deploy/compose/nginx.conf`](../../deploy/compose/nginx.conf), [`deploy/compose/docker-compose.yml`](../../deploy/compose/docker-compose.yml) (`cluster` profile) · Experiment: [E1](../reports/E1-per-instance-buckets.md)

## Problem
One gateway is a single point of failure and has a capacity ceiling, so production runs several (3 on Megatron). But anything a gateway keeps **in its own memory** (rate-limit buckets, caches) is now one of three copies, each seeing a third of the traffic. E1 measured what that does to a per-merchant limit: **2.99×**.

## Context
Every horizontally scaled service has this split:
- **Stateless parts scale freely.** Routing, auth checks against a cache, proxying: any instance can serve any request.
- **State that must be global** has to live somewhere shared (Redis, a database) or be *partitioned* so each key always goes to the same instance.

## Solution

### What a load balancer balances
| | L4 (transport) | L7 (application) |
|---|---|---|
| Sees | TCP connections (IP:port) | HTTP requests (method, path, headers) |
| Balances | **connections**: a request on an existing connection goes where the connection goes | **requests**: each one can go to a different backend |
| Examples | k8s `Service` (kube-proxy / iptables), AWS NLB | nginx, Envoy, HAProxy in HTTP mode, AWS ALB, k8s Ingress |
| Can route by path or header | No | Yes |

Clients reuse connections (HTTP keep-alive), and k6 does too. Behind **L4**, one VU's traffic can stay on one pod for the whole test, so E1 could show **1×** and hide the bug (spec §21). Behind **L7** nginx, each request is assigned separately, so the three buckets each receive a third of the traffic. That's why E1 runs locally behind nginx.

### Why the limit triples, exactly
```
noisy-corp: 100 req/s ──nginx round-robin──▶ gateway-1: ~33 req/s → own bucket allows 10/s → 608
                                         ├─▶ gateway-2: ~33 req/s → own bucket allows 10/s → 607
                                         └─▶ gateway-3: ~33 req/s → own bucket allows 10/s → 607
                                                                            total: 1,822 ≈ 3 × 610
```
Each instance is individually correct. The *system* is wrong because the limit is a property of the merchant, and no single instance sees all of a merchant's traffic.

### The ways out
1. **Shared state:** every instance checks one bucket in Redis, atomically. Flux does this in M5 ([ADR-0004](../adr/0004-redis-lua-server-clock.md)).
2. **Partitioning (consistent hashing, §20.10):** route each merchant to the same instance, e.g. nginx `hash $http_authorization consistent`. No shared store is needed, but one instance failing moves its merchants elsewhere with empty buckets, one hot merchant can overload "its" instance, and the gateway would have to route by key *before* authenticating it.
3. **Divide the limit by N:** each instance enforces `limit ÷ N`. That's cheap, and wrong whenever traffic isn't perfectly even or N changes. Flux uses it *only* as the degraded fallback when Redis is down (ADR-0005), where "roughly right" beats "unlimited".

## Alternatives
Listed above. Also *sticky sessions* (cookie- or IP-based affinity): the browser-app version of partitioning, with the same failover problems.

## Trade-offs
- **L7 costs more per request** (it parses HTTP), and the balancer must terminate the client's HTTP. nginx's overhead here is part of the cluster-profile latency.
- **Keep-alive to upstreams** (`keepalive 64` plus `proxy_http_version 1.1` and an empty `Connection` header) saves a TCP handshake per request. It doesn't defeat L7 balancing: nginx still picks the server per request and borrows an idle connection *to that server* from its pool.

## Implementation
- **The `cluster` profile:** `gateway-1/2/3`, configured from one YAML anchor (`x-gateway`), plus `nginx` on `:8080`.
- **A one-shot `migrate` service** runs `Flux.Gateway --migrate-only` (migrate, seed, exit) before any gateway starts (`service_completed_successfully`). Three gateways starting together never race to migrate the same database, the same rule M7 applies with a k8s Job.
- `access_log off` in nginx: at experiment rates, logging every request would measure the disk.

## Testing
E1 *is* the test: three runs at 2.99×, and a single-instance baseline at 1.00× (609 of 610). The per-instance counts come from each gateway's own `/metrics` and add up exactly to the client-side count.

## Observability
Summing `rate(flux_requests_total{outcome="allowed"}[1m])` across instances and comparing it with the plan would reveal this bug in production, but only per route group, since there's no merchant label. Per-merchant confirmation comes from logs.

## Related
[token-bucket](token-bucket.md) · [load-testing-with-k6](load-testing-with-k6.md) · later: redis-basics, lua-atomicity (M5) · ADR-0004, ADR-0005
