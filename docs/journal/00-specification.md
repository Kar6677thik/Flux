# Journal 00 — Specification run (M0)

- **Date:** 2026-10-15
- **Milestone:** M0
- **Code written:** none. That is the point of this milestone.

## What we did
1. Read the project brief (Project 1 in *10 Engineering Projects*) and the Field Guide sections it points to (7, 8, 15, 17, 25).
2. Took stock of the environment before designing anything:
   - **Laptop:** .NET SDK 10.0.401, Docker Desktop (installed but not running), Python 3.12 and `gh`, logged in. k6 is not installed, so we'll run it from its Docker image.
   - **Megatron (home server):** 8 cores, 7.6 GiB RAM, **83% disk used**. k3s v1.36 is running; there is no Docker. Tailscale Serve already maps `:443/` to Slate.
   - **Slate's existing CI/CD** (`slate.lib/.github/workflows/*.yml`) already deploys to this same k3s using GitHub Actions + Tailscale + SSH + a narrow sudo helper. That is a proven pattern to copy.
   - **The Flux GitHub repo is public.** That shapes how we handle secrets and dev keys.
3. Resolved the ambiguities in the brief and wrote down every decision: [spec.md](../spec.md) plus seven [ADRs](../adr/).

## Decisions you made
| Question | Your answer |
|----------|-------------|
| Who writes the code? | Claude builds in milestones; the docs teach; you review each milestone |
| How should it run on the server? | k3s, like Slate |
| Who can reach it? | Tailnet only |

## Decisions I made (each one can still be changed; reasons in the linked ADR or spec section)
| Ambiguity in the brief | Resolution | Where |
|------------------------|------------|-------|
| "ASP.NET Core or Python" | .NET 10 + YARP | ADR-0001 |
| What exactly "per merchant and per route" means | One bucket per `(merchant, route group)`; limits come from the plan; GET and POST share a group; limits are per merchant, not per key | Spec §5, §6 |
| What "10 requests/s" means for a token bucket | Refill rate 10/s **and** burst capacity 10. They are stored separately so they can diverge later. | Spec §6 |
| Which rate-limit headers | `X-RateLimit-Limit/Remaining/Reset` + `Retry-After`, using delta seconds | Spec §7.3 |
| How to authenticate | `Authorization: Bearer flux_…`, stored as SHA-256 | ADR-0003 |
| Fail open or closed | Payments: fail open to a reduced local limit. Refunds: fail closed. | ADR-0005 |
| Which status code when the limiter itself is down | `503`, not `429`: the client did nothing wrong | ADR-0005 |
| Should readiness check Redis? | **No.** Otherwise a Redis outage takes every gateway out of rotation. | Spec §11.2 |
| How to measure "latency the gateway adds" | Two independent ways: direct vs. through the gateway, and a `Server-Timing` header | Spec §11.2, P3 |
| Where the official benchmark numbers come from | Laptop Docker Compose, specs recorded; optional rerun on Megatron | Spec §15 |
| How to load-balance across 3 instances on k3s | A k8s Service, but the 3× demo runs locally behind nginx, because L4 balancing can hide the bug | Spec §21 |
| What the "two backend services" are | One stateless fake codebase, run as two services | ADR-0007 |
| How migrations run in production | A separate Job before rollout, not on gateway startup | Spec §17 |

## Why a specification first? (Why *now*)
- **Field Guide §1.1–1.2:** functional and non-functional requirements. Until "10 req/s" was defined precisely, a test for it couldn't be written.
- **§1.11, risk-driven development:** the riskiest unknowns were the deployment environment (no Docker, nearly full disk) and the failure semantics. Both are now settled on paper, where changing them is cheap.
- **§1.12 / §33.3, ADRs:** in six months, the *reasons* behind a design are harder to recover than the code. The ADRs also double as interview stories: "we chose X over Y because…".
- **§34, Speculative Generalization:** the non-goals list (spec §3) is equally important. It stops the project growing until it never finishes.

## Things this run uncovered that you need to know now
1. **Megatron's disk is 83% full.** Kubernetes starts garbage-collecting images at 85% and can evict pods near there. That threatens **Slate** too, not just Flux. Free some space before M7 (target ≥ 30% free).
2. **Some deployment steps need your hands:** sudo, GitHub secrets and the Tailscale admin. They are listed in spec §17, so M7 holds no surprises.
3. **Naming clash:** "Flux CD" is a well-known GitOps tool. If you ever mention Flux on your résumé, say "Flux (API gateway)" to avoid confusion.

## Before M1: read these (about 1 hour)
- The [Razorpay Edge talk page](https://2023.platformcon.com/talks/edge-at-razorpay-the-api-gateway-for-ddos-protected-high-scale-environments): the real problem.
- The [Redis post on throttling algorithms](https://redis.io/blog/api-throttling-algorithms-patterns.md): the mistakes we will reproduce in E2 and E1.
- Field Guide §7.5 (API Gateway), §15.7 (Reverse Proxy), §17.11–17.13 (rate limiting, throttling, token bucket), §17.21 (liveness vs. readiness).
- Skim [spec.md](../spec.md) §5 (request lifecycle) until you could explain it out loud.

## Exercises
1. Free plan, payments route: capacity 10, refill 10/s, bucket full at t = 0. A client sends 25 requests at t = 0, then 1 request at t = 0.35 s. How many are allowed in total, and what is `X-RateLimit-Remaining` on the last one?
2. Why does Flux hash API keys with SHA-256 when every password guide says to use bcrypt?
3. A fixed window of 10 s allows 100 requests. What is the most requests a client can get through within *any* 1-second span?
4. Why would making `/readyz` check Redis turn a Redis outage into a *total* outage?
5. Why does the Lua script read the time with Redis `TIME` instead of receiving it from the gateway?

<details><summary>Answers</summary>

1. At t = 0 the first 10 are allowed and 15 are rejected (`429`). By t = 0.35 s the bucket has refilled 0.35 × 10 = 3.5 tokens. The request spends 1, leaving 2.5 → **11 allowed in total**, `Remaining: 2` (floor of 2.5).
2. bcrypt is slow on purpose, to stop people guessing *low-entropy* human passwords. Our keys have 256 bits of randomness, so guessing is impossible anyway, and a slow hash would add latency to every request and make cache misses a DoS vector. (ADR-0003)
3. **200.** Send 100 just before the boundary and 100 just after. Both windows count 100 ≤ 100. (Spec §9.3; you'll measure this in E2.)
4. The load balancer removes unready instances. If every instance reports unready because Redis is down, there is nothing left to route to, even though payments were supposed to keep working in degraded mode. (Spec §11.2)
5. Gateways' clocks drift apart (clock skew). If each sent its own time, a gateway that is ahead would mint extra tokens. One clock, Redis's, gives one consistent answer. (ADR-0004)
</details>

## What could go wrong with this spec
- **The targets (N1–N6) may be unrealistic on a laptop running Docker Desktop.** That's fine. Reports state the real numbers honestly; a target is a hypothesis, not a promise.
- **Some choices may feel wrong once there is code to look at.** The process for changing one: write a new ADR that *supersedes* the old one, rather than silently editing it.

## Next: M1 — Skeleton
Solution structure, the fake backend, a gateway that only passes traffic through, Dockerfiles, Compose (`single` profile), and the CI workflow. Done when `curl` through the gateway reaches both backends and CI is green.
