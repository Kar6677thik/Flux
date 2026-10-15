# ADR-0007: One fake-backend codebase, run as two services

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M1)

## Context
The project calls for two backend services, "payments" and "refunds", behind the gateway. They exist only to have something to proxy to. Their behaviour is trivial and nearly identical.

## Decision
- **One project, `Flux.Backend`**, and one container image.
- **Two separate deployments** at runtime, with `Backend__ServiceName=payments` or `refunds`.
  - Each exposes only its own endpoints.
  - Each has its own address, so the gateway still routes to two distinct upstreams.
- **The backends are stateless.** `GET` returns a plausible fake object.
- **They reject requests that lack `X-Flux-Merchant-Id`.**

## Alternatives considered
| Option | Why not |
|--------|---------|
| Two separate projects | Duplicated code for no learning gain (DRY, Field Guide §2.6). |
| Reuse a warm-up API (notes/URL shortener) | The project guide suggests this, but no warm-up exists in the repos yet, and payment/refund-shaped endpoints make the demo clearer. |
| Store payments in PostgreSQL | Adds realism nobody measures. With 2+ backend replicas it would also need a shared database; with in-memory state, replicas would disagree. |

## Consequences
- The backends cost almost nothing to run, so they never become the bottleneck in experiments. That isolates what we measure: the gateway.
- `Backend__LatencyMs` simulates work. Its value is recorded in every report.

## Revisit when
A later project (for example Project 2's fraud service) supplies a real backend to put behind Flux.
