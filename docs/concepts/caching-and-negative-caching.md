# Caching and negative caching

> Field Guide: §12.1 Cache-Aside, §12.6 TTL, §12.9 Local In-Memory Cache, §12.11 Cache Stampede, §12.12 Cache Penetration, §12.15 Cache Consistency Strategy · Introduced in: M2
> Code: [`Auth/ApiKeyCache.cs`](../../src/Flux.Gateway/Auth/ApiKeyCache.cs), [`Auth/ApiKeyAuthenticator.cs`](../../src/Flux.Gateway/Auth/ApiKeyAuthenticator.cs), [`Tenancy/PlanCatalog.cs`](../../src/Flux.Gateway/Tenancy/PlanCatalog.cs)

## Problem
Every request needs "which merchant owns this key?". Asking PostgreSQL every time would put a network round trip and a query on the hot path, and it would make the database's capacity the gateway's capacity. Razorpay's auth budget is 5–8 ms; ours should be microseconds when the cache is warm.

## Context
Keys change rarely: a merchant is created, a key is rotated, an account is suspended. Requests are constant. That read-heavy, slowly changing shape is the textbook case for a cache.

## Solution

### Cache-aside with a TTL (§12.1, §12.6)
```
FindAsync(key):
  h = SHA-256(key)
  if cache has fresh h  → return cached answer        (no DB)
  answer = select … where key_hash = h and revoked_at is null
  cache[h] = answer for 60 s (found) or 10 s (not found)
  return answer
```
The application checks the cache, and on a miss loads from the database and fills the cache. That's *cache-aside*. Entries simply expire; nothing tells the cache when a row changes.

### Why the TTL *is* the consistency policy (§12.15)
With 3 gateway instances, each has its own in-memory cache. If an admin revokes a key:

| t | Gateway 1 (served the revoke) | Gateways 2 and 3 |
|---|------------------------------|-------------------|
| 0 s | row updated, cache still says "valid" | cache says "valid" |
| 0–60 s | key still works | key still works |
| ≤ 60 s | entry expires → DB → `401` | same, each at its own moment |

We could evict gateway 1's entry immediately, but gateways 2 and 3 would still serve it. Real invalidation needs a broadcast (Redis pub/sub, Postgres `LISTEN/NOTIFY`). Instead we **accept and document** 60 s of staleness (spec §8). The admin API states it, and `A_revoked_key_keeps_working_until_the_cache_ttl_then_gets_401` proves the exact boundary: still `200` at 59 s, `401` at 60 s.

### Negative caching (§12.12, cache penetration)
A cache that only stores *hits* does nothing for a key that doesn't exist: every request for it is a miss and goes to the database. So "not found" is cached too, for a shorter 10 s. Shorter because a key that appears later (just created, say) shouldn't be refused for long.

**What it does and doesn't protect against.** This is worth being precise about, because spec §8 makes it sound broader than it is:
- ✅ **The same bad key, over and over**: a client configured with a revoked key, or a retry loop. After the first miss, every repeat is a cache hit.
- ❌ **A flood of *different* random keys.** Each one is new, so each one is a miss and a query. Negative caching can't help with a key it has never seen. Real defences are rate limiting unauthenticated traffic by IP (a stretch goal), or a Bloom filter of valid key hashes that answers "definitely not a key" without the database.

### Bounding memory
Positive entries are bounded by the number of real keys in the database. Negative entries are bounded by *how many keys an attacker can invent*, which is unbounded. So `ApiKeyCache` keeps two maps and caps only the negative one (`MaxCacheEntries`, 100,000). When it's full, it first drops expired entries. If it's still full, it skips caching. A skipped entry costs exactly what no negative cache would have cost: one query.

### The plan catalog: a different shape
Plans are 2 rows and 4 limits, read on every request in M3. Instead of per-entry TTLs, `PlanCatalog` loads **all** of them at startup and **replaces the whole dictionary** every 60 s. Readers always see one consistent snapshot. Assigning a reference is atomic, so there's no lock. A failed refresh keeps the old snapshot.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| `IMemoryCache` | Built in, with size limits and eviction, and a fine choice. We'd have to plug a fake clock into its options to test exact TTL boundaries, and a single size limit can't express "cap only the negative entries". Our needs are two TTLs and one cap, so 70 lines we fully understand won. |
| `HybridCache` (.NET 9+) | In-memory plus a distributed L2, with stampede protection. Right if we wanted Redis to share the cache across instances. |
| Redis as a shared cache | One cache, so a revocation is visible everywhere at once. But a network hop on *every* request, and Redis becomes critical for auth too. ADR-0005 deliberately keeps Redis's failure scope to rate limiting. |
| Invalidate on write (pub/sub) | Near-instant revocation. A whole messaging path to build and test; worth it when 60 s is too long. |

## Trade-offs
- **Staleness** up to the TTL for revocations, suspensions and plan changes.
- **Cold starts.** A freshly started gateway sends every first request to the database. Fine at our scale; big systems warm caches before taking traffic (§12.13).
- **Stampede (§12.11).** If a popular key expires and 500 requests for it arrive in the same millisecond, all 500 miss and all 500 query. A *single-flight* loader (one query, everyone awaits its result) prevents that. We haven't built one: at learning scale it's a page of subtle code for no visible gain. Exercise 3 in journal 02 asks you to sketch it.

## Implementation
```csharp
// ApiKeyCache.TryGetFresh: expired entries are removed, but only the exact entry we read
map.TryRemove(new KeyValuePair<string, Entry>(keyHash, entry));
```
Between reading an expired entry and removing it, another request may already have refreshed it. `TryRemove(KeyValuePair)` removes only if the value is still the one we saw, so a fresh entry is never thrown away by mistake. The race is tiny, but it's the kind of thing that turns into a one-in-a-million production bug.

## Testing
- **Unit, at exact instants** ([`ApiKeyCacheTests`](../../tests/Flux.Gateway.UnitTests/ApiKeyCacheTests.cs)): with `FakeTimeProvider`, `Advance(59.999 s)` → hit, `Advance(1 ms)` → miss. No `Thread.Sleep`, no flakiness, and the test runs in microseconds.
- **Integration** ([`ApiKeyAuthTests`](../../tests/Flux.Gateway.IntegrationTests/ApiKeyAuthTests.cs)): the gateway's own `TimeProvider` is replaced with the fake, so the whole real path (HTTP → middleware → cache → PostgreSQL) shows revocation at 60 s, suspension at 60 s, and a key created *after* a cached miss starting to work at 10 s.

## Observability
Today: `Rejected unknown or revoked key` logs. M3 adds metrics; a useful one later would be cache hits/misses, because a falling hit rate is the early sign of random-key probing.

## Related
[api-key-authentication](api-key-authentication.md) · [multi-tenancy](multi-tenancy.md) · later: testing-time (M3), redis-basics (M5) · ADR-0008
