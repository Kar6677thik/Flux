# ADR-0003: API keys as Bearer tokens, stored as SHA-256 hashes

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M2)

## Context
Every request must be mapped to a merchant cheaply, within a few milliseconds. If the database leaks, stored keys must not be usable. The repo is public, so nothing secret can live in it.

## Decision
- **Header:** `Authorization: Bearer flux_<43 base62 chars>`. The key is 32 bytes from a cryptographically secure RNG (`RandomNumberGenerator`).
- **Storage:** `key_hash = SHA-256(full key)`, with a unique index, plus a non-secret `key_prefix` (first 12 characters) for humans and logs.
- **Visibility:** the full key is returned **once**, at creation, and never stored or logged.
- **Lookup:** hash the incoming key, then query the in-memory cache, then the database on a cache miss.

## Why SHA-256 and not bcrypt/Argon2?
Slow password hashes such as bcrypt and Argon2 protect **low-entropy secrets** (human passwords) against offline guessing. Our keys carry 256 bits of randomness, so guessing is impossible however fast the hash is. A slow hash would add tens of milliseconds to *every request*, and it would turn each cache miss into a CPU denial-of-service vector. GitHub, Stripe-style systems and most API-key systems use fast hashes for this reason.

## Alternatives considered
| Option | Why not |
|--------|---------|
| `X-API-Key` header | Works fine; `Authorization: Bearer` is the standard place for credentials, and proxies and log scrubbers already know to redact it. |
| Basic auth `key_id:key_secret` (Razorpay's real style) | Two values instead of one, and the `key_id` lookup would replace the hash index. Worth knowing about; not needed to learn the ideas. Mentioned in the concept note. |
| Store keys encrypted (reversible) | Anyone with the encryption key could read every API key. Hashing makes that impossible. |
| JWTs | Stateless tokens are hard to revoke (a core lesson of Project 4). Merchant API keys are long-lived and must be revocable. |

## Consequences
- A lost key cannot be recovered, only replaced. The admin API supports several keys per merchant so rotation needs no downtime.
- Revocation takes up to the cache TTL (60 s) to apply. Accepted and documented.

## Revisit when
We need per-key scopes (read-only keys, for example). Then scopes would be added to the key record.
