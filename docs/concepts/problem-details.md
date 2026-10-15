# Problem Details (RFC 9457) and the error contract

> Field Guide: §7.16 Consistent Error Contracts, §7.15 Request Validation, §25.17 Secure Error Handling, §7.19 Request Correlation · Introduced in: M1 (shape), M2 (full contract) · Code: [`Http/FluxProblems.cs`](../../src/Flux.Gateway/Http/FluxProblems.cs), [`Program.cs`](../../src/Flux.Gateway/Program.cs)

## Problem
A client that gets an error needs to know three things: *what kind* of error (to decide what to do), *a human explanation* (to log or show), and *a handle* to quote when asking for support. If every endpoint invents its own JSON, or returns an empty body or an HTML stack trace, clients end up parsing English.

## Context
RFC 9457 (which replaced RFC 7807) is the IETF standard for HTTP API errors: `application/problem+json` with `type`, `title`, `status`, `detail`, plus any extension fields. ASP.NET Core has built-in support (`ProblemDetails`, `AddProblemDetails`), and so do most API frameworks.

## Solution
```json
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json
WWW-Authenticate: Bearer realm="flux"

{
  "type": "https://flux.local/errors/missing_api_key",
  "title": "Missing API key",
  "status": 401,
  "detail": "Send your key as 'Authorization: Bearer flux_...'.",
  "code": "missing_api_key",
  "requestId": "2c0e5cc43ed84d65979c8607dfeb41fc"
}
```
| Field | For whom | Stable? |
|-------|----------|---------|
| `status` | HTTP machinery: retries, proxies | yes |
| `code` (extension) | **Client code**: `switch (code)` | yes, part of the API contract |
| `type` | Docs link, globally unique id of the error kind | yes |
| `title` / `detail` | Humans | no: wording may change |
| `requestId` (extension) | Support: "find me this request in the logs" | per request |

The rule clients should follow is **branch on `code`, never on `detail`**.

### Status codes say *whose* fault, and whether to retry
| | Client's fault, don't retry as is | Not the client's fault, retry later |
|---|---|---|
| auth | `401 missing_api_key / invalid_api_key`, `403 merchant_suspended` | `503 auth_unavailable` + `Retry-After` (ADR-0008) |
| input | `400 invalid_request`, `404 route_not_found` | |
| limits (M3/M5) | `429 rate_limit_exceeded` + `Retry-After` | `503 rate_limiter_unavailable` |
| upstream | | `502 upstream_unavailable`, `504 upstream_timeout` |
| bug | | `500 internal_error` |

## Alternatives
- **Your own error envelope** (`{ "error": { "code", "message" } }`, Stripe's style): perfectly good, and arguably simpler. A standard just means clients and tooling already know the shape.
- **GraphQL-style errors in a `200`**: hides failures from every HTTP-level tool (load balancers, k6 thresholds, metrics). Not for a REST gateway.

## Trade-offs
- **Every new failure needs a code.** M2 added five (`auth_unavailable`, `invalid_admin_token`, `merchant_not_found`, `key_not_found`, `internal_error`); they are listed in spec §7.4. Adding codes is cheap. *Renaming* one is a breaking change.
- **Errors can leak.** `detail` must never hold stack traces, SQL, hostnames or keys (§25.17).

## Implementation
- **One writer**: `FluxProblems.WriteAsync` (middleware) and the `FluxProblem` record (an `IResult` for endpoints) produce the same shape, so every path, including YARP failures, the 404 fallback and the exception handler, goes through the same code.
- **The exception handler** turns an unhandled exception into `500 internal_error` with the request id, and never a stack trace, even in Development.
- **Gotcha found in M2**: malformed JSON sent to a minimal API endpoint never reaches your handler, and what happens instead **depends on the environment**. In Development the framework throws `BadHttpRequestException`, which our exception handler first turned into a `500`. In Production it quietly answers `400` with an *empty* body, breaking the contract in a way a Development-only test would never notice. `RouteHandlerOptions.ThrowOnBadRequest = true` makes both environments throw, and the exception handler maps the exception to `400 invalid_request`. `Malformed_json_is_400_invalid_request` runs in both environments.
- **`401` carries `WWW-Authenticate`** (RFC 9110 requires it), with `error="invalid_token"` when a key was presented but rejected (RFC 6750).

## Testing
`Problems.AssertAsync` checks the status, the media type, the `code`, the `type` URL, and that `requestId` in the body equals the `X-Request-Id` header. Every row of the error table that exists so far has a test that calls it.

## Observability
The `requestId` in every error body is the same id that's in every log line for that request (M1's correlation middleware), so a client's bug report leads straight to the logs.

## Related
[api-key-authentication](api-key-authentication.md) · [middleware-pipeline](middleware-pipeline.md) · ADR-0008 · later: rate-limit-headers (M3)
