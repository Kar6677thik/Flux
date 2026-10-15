# API-key authentication

> Field Guide: §8.1 Authentication vs. Authorization, §8.15 Short-Lived Credentials (and why API keys aren't), §25.19 Secret Management, §25.25 Key Rotation, §25.18 Audit Logging · Introduced in: M2
> Code: [`Auth/ApiKey.cs`](../../src/Flux.Gateway/Auth/ApiKey.cs), [`Auth/ApiKeyAuthMiddleware.cs`](../../src/Flux.Gateway/Auth/ApiKeyAuthMiddleware.cs), [`Admin/AdminTokenFilter.cs`](../../src/Flux.Gateway/Admin/AdminTokenFilter.cs) · Decision: [ADR-0003](../adr/0003-api-keys-sha256-bearer.md)

## Problem
Until M2, any client could call `/v1/payments`. Worse, the backends would have believed any `X-Flux-Merchant-Id` a client chose to send, if M1 hadn't stripped it. The gateway needs to answer *"which merchant is this?"* for every request, quickly, and in a way that a database leak doesn't turn into a list of working credentials.

## Context
Server-to-server APIs (Stripe, Razorpay, GitHub, OpenAI) almost all use long-lived API keys. There's no human at the keyboard to do an OAuth dance, and the merchant's server stores the key in its own secret store. Flux's merchants are servers too.

## Solution

### Authentication ≠ authorization (Field Guide §8.1)
| Step | Question | Flux's answer | Failure |
|------|----------|---------------|---------|
| Authentication | *Who are you?* | The key maps to merchant `8d86…` on plan `pro` | `401` |
| Authorization | *May you do this?* | The merchant is `active` (later: and has tokens left) | `403` (later `429`) |

A suspended merchant *is* authenticated, since we know exactly who they are, and that's why they get a `403`, not a `401`. The HTTP spec makes the same split: `401` means "your credentials are missing or wrong" and must carry a `WWW-Authenticate` header that says how to authenticate. `403` means "I know who you are; no."

### The key itself
```
flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT
└─┬─┘└──────────────────── 43 base62 chars ─────┘
scheme            (32 random bytes = 256 bits)
└──── prefix ─────┘  "flux_devQuie" = first 12 chars, safe to log and show
```
- **A recognisable scheme** (`flux_`) means secret scanners (GitHub's, gitleaks) can spot a leaked Flux key in a commit, and the format check can reject junk with no lookup at all.
- **Fixed length.** Base62 of a random 256-bit number is *usually* 43 characters, but about 1 in 62 numbers is small enough to need only 42. `ToBase62` left-pads with `0`, so the check can be an exact `== 48`.
- **The prefix** is how humans and logs refer to a key ("which key is this?") without ever handling the secret.

### Store a hash, not the key
```
POST /admin/merchants ─▶ key = Generate()           (shown once, in this response only)
                          store SHA-256(key), prefix
request with key ──────▶ SHA-256(key) ─▶ cache ─▶ (miss) select … where key_hash = $1
```
If the `api_keys` table leaks, an attacker has hashes. To use one they would have to find a key with that SHA-256. With 256 random bits that's impossible, so a *fast* hash is enough. ADR-0003 covers why bcrypt would only make every cache miss slower.

### Comparing secrets: when constant time matters
- **API keys: no comparison at all.** The database finds the row through a unique index on the hash. No code ever compares the secret character by character, so there's no timing signal to leak.
- **The admin token *is* compared**, because it's one configured value. A plain `==` stops at the first differing character, so a wrong first character answers measurably faster than a wrong last character. Over many tries that leaks the token one character at a time. `CryptographicOperations.FixedTimeEquals` always looks at every byte. `AdminTokenFilter` also hashes both sides first: `FixedTimeEquals` returns immediately when the lengths differ, which would leak the token's *length*.

### Don't pass the secret along
After a successful lookup the middleware **removes `Authorization`** before YARP forwards the request. The backends trust `X-Flux-Merchant-Id` and have no use for the key. Every extra hop a credential travels is one more log, trace or crash dump it can end up in.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| `key_id:key_secret` with Basic auth (Razorpay's real scheme) | The public `key_id` finds the row, then the secret is checked against a stored hash. Two values to manage, but the id can appear in logs and dashboards. Our prefix plays that role. |
| HMAC request signing (AWS SigV4) | The secret never travels at all: the client signs each request. Much stronger against replay and interception, but much harder for clients to implement. |
| JWTs / OAuth client credentials | Short-lived tokens that verify without a lookup. Revocation is the hard part (Project 4), and you still need a long-lived credential to *get* a token. |
| mTLS client certificates | Strong, but certificate issuance and rotation for every merchant is a whole product. |

## Trade-offs
- **Bearer means bearer.** Anyone holding the key *is* the merchant. TLS (Tailscale Serve, M7) protects it in transit; the merchant protects it at rest.
- **Shown once.** A lost key can't be recovered, only replaced. That's why the admin API supports several keys per merchant: create the new key, switch the client over, revoke the old one, with no downtime (`A_second_key_allows_rotation_without_downtime`).
- **Revocation isn't instant.** Up to 60 s, because of the cache (see [caching-and-negative-caching](caching-and-negative-caching.md)).
- **Limits are per merchant, not per key** (spec §5), so creating extra keys never buys extra quota.

## Implementation
```csharp
// ApiKeyAuthMiddleware: runs inside YARP's pipeline, i.e. only for matched public routes
if (!TryReadKey(http.Request, out var key))           → 401 missing_api_key  (no lookup at all)
merchant = await authenticator.FindAsync(key, ct);    // hash → cache → DB
  DB unavailable                                      → 503 auth_unavailable (ADR-0008)
if (merchant is null)                                 → 401 invalid_api_key  (unknown OR revoked)
if (merchant.Status == Suspended)                     → 403 merchant_suspended
headers[X-Flux-Merchant-Id] = merchant.MerchantId;    // the only identity backends trust
headers.Remove(Authorization);
```
- **Unknown and revoked look identical** to the client. Telling them apart ("this key *was* valid") helps an attacker more than a legitimate user.
- **Placement:** inside `MapReverseProxy(proxy => …)`, not in the main pipeline. Spec §5 says route matching comes *before* auth. `/healthz` and `/admin/*` never ask for a merchant key, and `GET /v1/nope` is a `404`, not a `401`.

## Testing
[`ApiKeyAuthTests`](../../tests/Flux.Gateway.IntegrationTests/ApiKeyAuthTests.cs), all against real PostgreSQL:
- every `401`/`403` row of the error table, including four kinds of malformed header;
- the upstream receives the merchant headers and **no** `Authorization`;
- a forged `X-Flux-Merchant-Id` is overwritten (`PassThroughProxyTests`);
- `Keys_are_stored_only_as_a_sha256_hash_and_a_short_prefix` reads the row back and checks it holds a hash and a prefix, not the key.

[`ApiKeyTests`](../../tests/Flux.Gateway.UnitTests/ApiKeyTests.cs) checks the format over 1,000 generated keys, so the rare 42-character case gets exercised.

## Observability
- Rejections log at `Information` with `keyPrefix`: `Rejected unknown or revoked key flux_0000000`. A burst of these from one prefix means a misconfigured client or a stolen key being probed.
- After auth, every log line in that request carries `merchantId` and `keyPrefix` (logging scope).
- **No log line ever holds a full key.** Verify it yourself: `docker compose logs gateway | grep -cE "flux_[0-9A-Za-z]{43}"` should print `0`.

## Related
[multi-tenancy](multi-tenancy.md) · [caching-and-negative-caching](caching-and-negative-caching.md) · [problem-details](problem-details.md) · [middleware-pipeline](middleware-pipeline.md) · ADR-0003, ADR-0008
