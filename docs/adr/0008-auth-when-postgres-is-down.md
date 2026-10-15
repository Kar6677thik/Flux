# ADR-0008: When PostgreSQL is unavailable, uncached keys get `503 auth_unavailable`

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M2

## Context
Authentication reads PostgreSQL only on a cache miss (spec §8). The spec says what happens when Redis fails (ADR-0005) but not when PostgreSQL fails. M2's first test of this case returned a bare `500`: EF Core wraps a refused connection in an `InvalidOperationException`, which nothing caught.

During a PostgreSQL outage a gateway still has:
- every key seen in the last 60 s, in its positive cache;
- every unknown key seen in the last 10 s, in its negative cache;
- the plan catalog, which keeps its last good snapshot when a refresh fails.

So the open question is narrow: what does a request get when its key is **not** in the cache and the database can't be asked?

## Decision
- **Cached keys keep working.** Nothing extra was built for this; it follows from the cache design.
- **An uncached key gets `503 Service Unavailable`**, `code: auth_unavailable`, with `Retry-After: 5`. The response is Problem Details, like every other error.
- The failure is logged at `Error` with the key **prefix** only.
- Detection: `DbException`, `TimeoutException`, or EF Core's `InvalidOperationException` wrapping a `DbException`. Any other exception stays a `500 internal_error`, because it's a bug, not an outage.
- **Readiness does not change.** `/readyz` reports that the plans were loaded *once*. A gateway that loses PostgreSQL later stays in rotation, because it can still serve every cached merchant.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Let it be a `500` | Says "the gateway is broken", which isn't true, and gives the client no hint to retry. |
| `401 invalid_api_key` | A lie: the key may be perfectly valid. Clients that treat 401 as "rotate the key" or "page a human" would react wrongly. |
| Fail open (let the request through unauthenticated) | We wouldn't know *who* is calling, so there would be no merchant id to forward and no limit to apply. Never acceptable for authentication. |
| Serve expired cache entries during an outage ("stale-if-error", Field Guide §12.14) | Attractive: a merchant whose entry expired a second before the outage would keep working. But it also keeps *revoked* keys alive for as long as the outage lasts, and it needs extra state ("expired but kept"). Worth revisiting if outages turn out to be common. |
| Mark the gateway unready while PostgreSQL is down | Every gateway would leave the load balancer at once, a total outage, and cached merchants would lose service too. Same reasoning as "readiness ignores Redis" (spec §11.2). |

## Consequences
- During an outage, merchants with recent traffic keep working. A merchant whose first request in a while arrives during the outage gets a retryable `503`.
- `auth_unavailable` joins the error table in spec §7.4.
- The outage is visible as `Error` logs. M3's `flux_requests_total` will need an outcome for it.
- A **slow** database (rather than a refused connection) still holds the request until Npgsql's connect timeout (15 s by default). M5 is the milestone about timeouts. If that wait proves painful, we'll lower `Timeout=` in the connection string.

## Revisit when
- Measured outages show many merchants hitting `503` just after their cache entries expire → consider stale-if-error with a short cap.
- We add a second source of truth for keys (a read replica, say) → fall back to it before giving up.
