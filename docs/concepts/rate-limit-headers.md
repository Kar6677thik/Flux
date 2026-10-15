# Rate-limit headers

> Field Guide: §7.16 Consistent Error Contracts, §17.11 Rate Limiting, §16 (clock skew) · Introduced in: M3
> Code: [`RateLimiting/RateLimitHeaders.cs`](../../src/Flux.Gateway/RateLimiting/RateLimitHeaders.cs), [`RateLimiting/RateLimitMiddleware.cs`](../../src/Flux.Gateway/RateLimiting/RateLimitMiddleware.cs) · Spec §7.3

## Problem
A client that only finds out about limits by getting `429`s retries blindly. It either hammers the API (making things worse) or backs off far too long. Well-behaved clients need to *see* their budget on every response and be told exactly when to come back.

## Context
GitHub, Twitter/X, Stripe, Shopify and Discord all send some form of `X-RateLimit-*`. `Retry-After` is standard HTTP (RFC 9110 §10.2.3), and HTTP clients and libraries (Polly, many SDKs) honour it automatically.

## Solution
```
HTTP/1.1 429 Too Many Requests              ← measured in M3's Compose run
Retry-After: 1
X-RateLimit-Limit: 2
X-RateLimit-Remaining: 0
X-RateLimit-Reset: 1
Content-Type: application/problem+json
{"code":"rate_limit_exceeded","detail":"Merchant plan 'free' allows 2 requests/s on route group 'refunds'.", …}
```
| Header | Meaning | On |
|--------|---------|----|
| `X-RateLimit-Limit` | Capacity B: the most you could send in one instant | every rate-limited response |
| `X-RateLimit-Remaining` | Whole tokens left *after* this request | every rate-limited response |
| `X-RateLimit-Reset` | Seconds until the bucket is completely full | every rate-limited response |
| `Retry-After` | Seconds until at least one token exists | `429` and `503` only |
| `X-RateLimit-Degraded: true` | The local fallback made this decision (M5) | only then |

### Two rules that look small but matter
1. **Delta seconds, not timestamps.** `Reset: 1` means "in one second", not "at 1760518800". A timestamp only works if the client's clock agrees with ours. Mobile devices and misconfigured servers are often seconds or minutes off, and a skewed client would either hammer or wait. A delta is immune to that.
2. **Always round up.** A client that waits exactly what we advertise must find what we promised. 70 ms becomes `1`, never `0`, and `Retry-After` is at least `1`. Rounding *down* would invite an immediate retry that fails again.

## Alternatives
- **The IETF draft `RateLimit` / `RateLimit-Policy` headers** (draft-ietf-httpapi-ratelimit-headers). The draft has changed shape several times: early versions used three separate `RateLimit-Limit/Remaining/Reset` headers, newer ones a single structured `RateLimit: "default";r=7;t=1` plus `RateLimit-Policy: "default";q=10;w=1`. It's the future, but it isn't settled. We send the widely understood `X-` names and would add the draft headers alongside them once it's an RFC.
- **Only `Retry-After`, no budget headers.** That's enough for correctness, but a client can't pace itself before hitting the wall.

## Trade-offs
- **Limit = burst capacity, not "per second".** With `B = r` (our plans) they're the same number. If a plan ever had B ≠ r, `Limit: 50` would no longer mean "50/s". The draft's `RateLimit-Policy` exists to describe exactly that.
- **Information disclosure.** Headers reveal the plan's limits. They're a published contract anyway.

## Implementation
- **`Response.OnStarting`, not "set now".** For an allowed request the backend's response is what goes out, and YARP copies the backend's headers onto the response when it forwards. Headers set *before* forwarding could be overwritten. Headers set in `OnStarting` are applied at the last moment before the first byte, after YARP's copy, so they land on the backend's `200` and the gateway's own `429` alike.
- `RateLimitHeaders.CeilingSeconds` rounds to 6 decimals before `Math.Ceiling`, so `1.0000000001 s` (floating-point noise) is `1`, not `2`.
- **Only rate-limited routes** carry them. `/healthz`, `/metrics`, `/admin/*`, `404`s and `401`s have no bucket, so they have no budget to report (`Gateway_endpoints_and_unknown_routes_have_no_rate_limit_headers`).

## Testing
- Unit ([`RateLimitHeadersTests`](../../tests/Flux.Gateway.UnitTests/RateLimitHeadersTests.cs)): rounding at 0, 70, 1000 and 1001 ms; `Retry-After` only when refused; `Degraded` only for fallback decisions.
- Integration ([`RateLimitContractTests`](../../tests/Flux.Gateway.IntegrationTests/RateLimitContractTests.cs)): exact header values on the 1st and the 11th request, and the `429` Problem Details body word for word.

## Observability
A client that ignores `Retry-After` is visible as a high `limited`/`allowed` ratio. k6 in M4 records these headers per request.

## Related
[token-bucket](token-bucket.md) · [problem-details](problem-details.md) · later: fail-open-vs-fail-closed (M5, `Degraded` and the `503`)
