# The ASP.NET Core middleware pipeline

> Field Guide: §3 Chain of Responsibility, §2.9 Separation of Concerns, §7.19 Request Correlation, §25.4 Input Validation · Introduced in: M1
> Code: [`src/Flux.Gateway/Program.cs`](../../src/Flux.Gateway/Program.cs), [`Http/`](../../src/Flux.Gateway/Http/)

## Problem
A gateway does many independent things to each request: tag it with an id, clean headers, authenticate, rate-limit, forward, format errors. If all of that lived in one big handler, every change would touch everything, and the *order*, which matters for correctness and security, would be implicit.

## Context
ASP.NET Core, Express, Django, Go's `net/http` handlers and nginx phases are all variations of one idea: a request passes through an ordered chain of small steps.

## Solution
A middleware is a function `(HttpContext, next) → Task`. It can:
1. look at or change the request, then
2. call `next(http)` to pass it down the chain (or **not**, which *short-circuits*, e.g. returning a 401), then
3. look at or change the response on the way back up.

```
request ─▶ ExceptionHandler ─▶ CorrelationId ─▶ StripFluxHeaders ─▶ routing ─┬─▶ /healthz, /readyz, /admin/*
                                                                             ├─▶ YARP route: RouteGroup ─▶ Auth (M2) ─▶ RateLimit (M3) ─▶ UpstreamError ─▶ UpstreamTiming ─▶ forward
                                                                             └─▶ fallback 404
```
*Updated in M2:* auth lives **inside YARP's own pipeline** (`MapReverseProxy(proxy => …)`), not in the main one. It therefore runs only after a public route has matched (spec §5: route match is step 3, auth step 4), and gateway endpoints never ask for a merchant key.

This is the Chain of Responsibility pattern, built into the framework.

## Why the order in Flux is what it is
| Position | Middleware | Why here |
|----------|-----------|----------|
| 0 | `UseExceptionHandler` (M2) | Outermost, so an exception anywhere below becomes a `500 internal_error` Problem Details response, never a stack trace. |
| 0′ | `RequestTelemetryMiddleware` (M3) | Starts the clock as early as possible and writes `Server-Timing` and the request metrics; see [metrics-and-cardinality](metrics-and-cardinality.md). |
| 1 | `CorrelationIdMiddleware` | Everything after it, including error responses, needs the request id. |
| 2 | `StripFluxHeadersMiddleware` | Must run **before** anything trusts `X-Flux-*`. If auth ran first and then something read a client-supplied `X-Flux-Merchant-Id`… game over. |
| 2′ | `RouteGroupMiddleware` (M3), first in the YARP pipeline | Spec step 3: reads the matched route's group, so even a `401` can be labelled "payments". |
| 3 | Auth (M2), in the YARP pipeline | Must know *who* before deciding *how much*. Placed after route matching, so `/v1/nope` is a `404`, not a `401`. |
| 4 | Rate limit (M3), in the YARP pipeline | Needs the merchant from auth. It comes before forwarding, because a rejected request must never cost a backend anything. |
| 5 | Endpoints: `/healthz`, `/readyz`, `/admin/*`, YARP forward, fallback | The actual work. `/admin/*` uses an *endpoint filter* for its token check. |

## Alternatives
- **MVC action filters**: per-controller, so they don't apply to YARP's routes.
- **Endpoint filters** (used in `Flux.Backend` for `RequireMerchant`): scoped to a group of endpoints. They're right when only *some* endpoints need a rule; middleware is right when *everything* passes through.

## Trade-offs
- Ordering bugs are silent. Swap two `UseMiddleware` lines and everything still compiles. Integration tests (not unit tests) are what catch that.
- Each layer adds a little latency. It's negligible here, and P3 measures the total.

## Implementation highlights
- **`Response.OnStarting`**: response headers can only be changed *before* the first byte is sent. `CorrelationIdMiddleware` registers a callback that runs just before that moment, so `X-Request-Id` lands on every response, even ones written by YARP or the fallback.
- **Validating client input that ends up in logs.** A client may send its own `X-Request-Id` (useful for tracing across *their* systems too), but we only keep it if it's 1–64 characters of `[A-Za-z0-9_-]`. Otherwise someone could send newlines or megabytes into our logs (log injection). `SearchValues<char>` makes that check fast.
- **Logging scopes**: `logger.BeginScope({ requestId })` attaches the id to every log line written further down the chain, without passing it around by hand.
- **Removing headers safely**: `StripFluxHeadersMiddleware` copies the matching keys into a list (`.ToList()`) before removing them. You can't modify a collection while enumerating it.

## Testing
- `Strips_client_supplied_flux_headers_so_merchants_cannot_be_spoofed` sends `X-Flux-Merchant-Id` and `x-flux-plan` (note the different case: header names are case-insensitive) and asserts the upstream saw neither.
- `Keeps_a_client_request_id_only_if_it_is_safe` covers a good id, spaces, a non-ASCII character, and an over-long id.

## Observability
Every log line carries `requestId`. In JSON logs (non-Development) that's a field you can filter on.

## Related
[reverse-proxy](reverse-proxy.md) · [api-key-authentication](api-key-authentication.md) · later: token-bucket (M3)
