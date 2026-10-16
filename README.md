# Flux

A multi-tenant API gateway and rate limiter, built in ASP.NET Core and YARP, with Redis-backed token buckets per merchant and per route. Inspired by Razorpay's Edge gateway. It is a learning project: Project 1 of *10 Engineering Projects from Real Company Problems*.

> **Status:** M4, "break it" done. Measured so far: 3 gateways with in-memory buckets let a merchant through at **2.99×** its limit ([E1](docs/reports/E1-per-instance-buckets.md)), and a fixed window allowed **200 requests in ~235 ms** against a 10 req/s plan ([E2](docs/reports/E2-fixed-window-boundary.md)). M5 fixes the first with Redis.
> Start with **[docs/spec.md](docs/spec.md)**, or with **[docs/README.md](docs/README.md)** for the full reading order.

## What it does (once built)
- Authenticates each request by API key (`Authorization: Bearer flux_…`) and maps it to a merchant.
- Enforces per-merchant, per-route limits from the merchant's plan (free: 10 req/s on payments; pro: 100 req/s).
- Returns `429` with `Retry-After` and `X-RateLimit-*` headers when a limit is hit.
- Shares limits across gateway instances through one atomic Redis Lua script.
- Fails open (with a reduced local limit) for payments and fails closed for refunds when Redis is down.
- Deploys to a home k3s cluster via GitHub Actions over Tailscale.

## Milestones
M0 Spec ✅ · M1 Skeleton ✅ · M2 Tenancy & auth ✅ · M3 Token bucket ✅ · M4 Break it ✅ · M5 Redis + resilience · M6 Prove it · M7 Deploy

The results (measured numbers only) will appear here after M6.
