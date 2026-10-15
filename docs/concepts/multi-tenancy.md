# Multi-tenancy

> Field Guide: §8.13 Tenant Isolation, §9.20 Shared Tables with Tenant IDs, §20.15 Noisy Neighbor Isolation, §20.16 Per-Tenant Quotas, §25.37 Tenant Data Isolation · Introduced in: M2
> Code: [`Data/Entities.cs`](../../src/Flux.Gateway/Data/Entities.cs), [`Data/FluxDbContext.cs`](../../src/Flux.Gateway/Data/FluxDbContext.cs), [`Tenancy/`](../../src/Flux.Gateway/Tenancy/), [`Auth/AuthenticatedMerchant.cs`](../../src/Flux.Gateway/Auth/AuthenticatedMerchant.cs)

## Problem
One gateway serves many merchants. Each one must be identified, held to *its own* limits, and kept from acting as, or slowing down, any other. Without an explicit tenancy model, "who is this?" and "how much may they do?" end up as scattered `if` statements.

## Context
Every SaaS API is multi-tenant: Razorpay's merchants, Stripe's accounts, Slack's workspaces. The tenant is the unit of billing, limits and isolation. In Flux the tenant is the **merchant**.

## Solution
```
plans (free, pro)  ──< plan_route_limits (plan × route group → refill, burst)
  ▲
  │ plan_id
merchants (id, name, plan_id, status) ──< api_keys (prefix, hash, revoked_at)
```
- **Merchant** = tenant: one plan, `active` or `suspended`.
- **Plan** = a named bundle of limits, one per **route group**. Merchants don't own limits directly, so changing what "pro" means is one row update, not thousands.
- **API key** = proof of identity. *Many* keys per merchant (for rotation), but **limits attach to the merchant, not the key**. Otherwise ten keys would buy ten times the quota.

### How the tenant travels through a request
```
Authorization: Bearer flux_…  ──▶ auth ──▶ AuthenticatedMerchant(id, plan, status, prefix)
                                            ├─ HttpContext.Items  (M3's rate limiter reads it here)
                                            └─ X-Flux-Merchant-Id / X-Flux-Plan → backend
```
The gateway is the **only** place that decides who the tenant is. Backends receive the answer in `X-Flux-*` headers and trust it. That's safe only because (1) clients can't set those headers (M1 strips them) and (2) clients can't reach the backends directly (Compose network, later a NetworkPolicy).

### Isolation models (§9.18–9.20)
| Model | Isolation | Cost | Flux? |
|-------|-----------|------|-------|
| Shared tables + tenant id column | Logical: every query must filter by tenant | Cheapest, one schema | ✅ (the gateway's own data) |
| Schema per tenant | Stronger; migrations run N times | Medium | |
| Database per tenant | Strongest; noisy queries can't cross | Most operations work | |

Flux's gateway stores *about* tenants (plans, keys), not tenant business data, so shared tables are the obvious fit. The isolation that matters most here is **traffic** isolation: one merchant's flood must not starve others. That's M3–M6: per-merchant buckets, then experiment P2.

## Alternatives
- **Per-merchant limit overrides** (a big customer negotiates more): a nullable override table. Deliberately a stretch goal (spec §3).
- **Tenant from the URL or subdomain** (`acme.api.example.com`): common for browser apps, but the key already identifies the merchant, and two sources of truth can disagree.

## Trade-offs
- **Plan changes apply within 60 s, not instantly**: the auth cache holds the plan id ([caching](caching-and-negative-caching.md)).
- **Suspension is a status, not a deletion.** History stays, keys stay valid in the table, and un-suspending is one `PATCH`. The `403` is decided at the gateway, so backends never see suspended traffic.
- **Trusting headers** moves all tenant security to the gateway plus the network boundary. Defence in depth (§25.2) would have backends verify a signed token instead. That's more machinery, and ADR-0007 keeps the backends deliberately dumb.

## Implementation
- **Constraints in the schema, not only in C#** (§9.7): `check (status in ('active','suspended'))`, `check (refill_per_second > 0)`, a unique index on `key_hash`. `The_database_itself_rejects_an_invalid_merchant_status` bypasses EF with raw SQL and shows PostgreSQL refusing the row.
- **IDs are UUIDv7** (`Guid.CreateVersion7`): unique without coordination like any UUID, but time-ordered, so new rows land at the end of the primary-key index instead of at random pages.
- **Demo tenants** (`quiet-shop` pro, `noisy-corp` free, `suspended-co`) are seeded **only in Development**, with fixed keys. The keys are in this public repo, so the gateway refuses to start if seeding is enabled anywhere else (`Dev_seeding_outside_development_refuses_to_start`).

## Testing
- Each integration test **creates its own merchant** through the admin API (`GatewayFactory.CreateMerchantAsync`). Tests run in parallel against one database, and a test that suspends "quiet-shop" would break every other test using it.
- `The_seeded_dev_merchants_authenticate_with_their_plans` checks the seed and the plan header together.

## Observability
`merchantId` is in the log scope of every request after auth. It is **not** a metric label (spec §11.2): with thousands of merchants that's thousands of time series per metric (M3's cardinality note).

## Related
[api-key-authentication](api-key-authentication.md) · [ef-core-migrations](ef-core-migrations.md) · later: token-bucket (M3), noisy-neighbour-isolation (M6)
