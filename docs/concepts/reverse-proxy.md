# Reverse proxy (and YARP)

> Field Guide: §15.7 Reverse Proxy, §7.5 API Gateway, §15.12–13 Connection pooling / keep-alive · Introduced in: M1
> Code: [`src/Flux.Gateway/appsettings.json`](../../src/Flux.Gateway/appsettings.json), [`Program.cs`](../../src/Flux.Gateway/Program.cs), [`Proxy/UpstreamErrorMiddleware.cs`](../../src/Flux.Gateway/Proxy/UpstreamErrorMiddleware.cs)

## Problem
Without a single front door, every backend service has to authenticate, rate-limit, log and expose itself to the internet on its own. Each one gets a slightly different version of the same rules, and clients need to know every backend's address.

## Context
Every API company runs some kind of front door: Razorpay's Edge, Stripe's API gateway, an nginx in front of a monolith. In Flux the gateway is the *only* thing clients talk to. `payments` and `refunds` aren't even reachable from outside.

## Solution
A **forward proxy** acts for *clients* (a corporate proxy that filters what employees browse). A **reverse proxy** acts for *servers*: clients think they're talking to the proxy, and the proxy picks a backend, forwards the request and relays the response.

```
client ──HTTP──▶ gateway :8080 ──HTTP──▶ payments:8080 /payments
                 (rewrite /v1/payments → /payments, add X-Request-Id, strip X-Flux-*)
```

An **API gateway** is a reverse proxy that also enforces API policy: authentication, rate limits, the error format, and versioning (`/v1`).

YARP's configuration has two halves:
- **Routes** answer: which incoming requests does this rule match (path, method), and how is the request transformed?
- **Clusters** answer: where do matched requests go (one or more *destinations*), and with which HTTP settings (timeouts)?

Flux's four routes map exactly to the public API table in spec §7.1. Each one carries `Metadata.RouteGroup`, which M3's rate limiter will read to pick the bucket.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| nginx / Envoy / HAProxy | Faster and battle-hardened, but custom logic means Lua (OpenResty), C++ filters (Envoy) or WASM. |
| Kong, Tyk, cloud API gateways | Plugins for everything; you learn the product rather than the problem. |
| Hand-written `HttpClient` forwarding | You'd rediscover every edge case YARP already handles: hop-by-hop headers, streaming bodies, connection reuse, cancellation. |
| No gateway: each service does it all | Rules drift between services, and it's hard to change one thing everywhere. |

## Trade-offs
- **An extra network hop on every request.** Experiment P3 measures this "gateway overhead".
- **A single point of failure.** We'll run 3 instances (M4) and keep its dependencies out of readiness (spec §11.2).
- **The gateway becomes a choke point for change.** Every team needs its routes in here; Razorpay solves this with plugins and config.

## Implementation
The interesting part of the request path is just config:

```json
"payments-get": {
  "ClusterId": "payments",
  "Match": { "Path": "/v1/payments/{id}", "Methods": [ "GET" ] },
  "Metadata": { "RouteGroup": "payments" },
  "Transforms": [ { "PathPattern": "/payments/{id}" } ]
}
```

- `PathPattern` rebuilds the upstream path from route values, which is how `/v1` is dropped.
- `PathSet` replaces the path outright; it's used for the exact-match `POST` routes.
- The cluster's `HttpRequest.ActivityTimeout` (10 s) is how long YARP waits for the upstream before giving up. When that happens YARP sets status 504 and records `ForwarderError.RequestTimedOut`.

What YARP does for us without being asked:
- It **pools connections** to each destination (`SocketsHttpHandler`), so repeated requests reuse warm TCP connections instead of paying a handshake each time.
- It **streams bodies**. A large request body flows through without being buffered whole in memory.
- It **drops hop-by-hop headers**: `Connection`, `Keep-Alive` and `Transfer-Encoding` describe one TCP hop, not the whole journey.
- It **adds `X-Forwarded-For/Proto/Host`**, so backends can see the original client.
- It **propagates cancellation**. If the client disconnects, the upstream request is cancelled too.

What YARP does *not* do nicely by default: on failure it sets 502/504 with an **empty body**. `UpstreamErrorMiddleware` runs *inside* YARP's proxy pipeline (`MapReverseProxy(proxy => proxy.UseMiddleware<…>())`). After forwarding, it reads `IForwarderErrorFeature` and writes our Problem Details instead (spec §7.4).

Unknown routes (`GET /v1/nope`, or a `DELETE` that no route allows) fall through to `MapFallback`, which returns `404 route_not_found`. Strictly, a wrong method on a known path deserves `405 Method Not Allowed`. We accept the simpler 404 for now.

## Testing
[`PassThroughProxyTests`](../../tests/Flux.Gateway.IntegrationTests/PassThroughProxyTests.cs) runs the real gateway in-process against [`EchoUpstream`](../../tests/Flux.Gateway.IntegrationTests/EchoUpstream.cs): a tiny real HTTP server that answers with exactly what it received. That makes the path rewrite (`/v1/payments/pay_123` → `/payments/pay_123`) directly assertable. The failure cases point the cluster at a dead port (→ 502) or at an endpoint slower than a 300 ms timeout (→ 504).

The echo server must be a *real socket*, not ASP.NET's in-memory `TestServer`, because YARP makes genuine HTTP calls.

## Observability
- `502 upstream_unavailable` / `504 upstream_timeout` rates tell you whether the gateway or a backend is at fault.
- `X-Request-Id` connects the gateway log line with the backend log line for the same request.
- M3 adds `Server-Timing: gw;dur=…, upstream;dur=…` to split each request's time between the gateway and the backend.

## Related
[middleware-pipeline](middleware-pipeline.md) · [docker-compose](docker-compose.md) · later: load-balancing (M4), timeouts-and-circuit-breakers (M5)
