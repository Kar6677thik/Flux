# Flux documentation

These docs record **what** was built, **why** it was built that way, and **when** each piece was added. They're written to be read alongside your *Software Engineering Field Guide* (referred to as "Field Guide §N").

## Reading order

1. **[spec.md](spec.md)**: the whole system on one page. Start here.
2. **[glossary.md](glossary.md)**: keep it open while reading anything else.
3. **[journal/](journal/)**: the story, one entry per milestone, read in order. Each entry covers:
   - **What** we did and **why**
   - **Why now**: why this step came at this point in the order
   - Concepts introduced, with Field Guide links
   - How to verify it yourself (commands)
   - Exercises, with answers you can expand
   - What could go wrong
4. **[adr/](adr/)**: Architecture Decision Records, one per significant choice, with the options we rejected and why.
5. **[concepts/](concepts/)**: one note per concept, in the Field Guide's template (Problem, Context, Solution, Alternatives, Trade-offs, Implementation, Testing, Observability, Related). They are written as each concept enters the code.
6. **[reports/](reports/)**: the Break/Prove experiment reports, with numbers, machine specs and the exact commands used.

## Index

| Journal | Milestone | Status |
|---------|-----------|--------|
| [00 — Specification](journal/00-specification.md) | M0 | ✅ Done, awaiting your review |
| 01 — Skeleton | M1 | Next |
| 02 — Tenancy & auth | M2 | |
| 03 — In-memory token bucket | M3 | |
| 04 — Break it | M4 | |
| 05 — Redis + resilience | M5 | |
| 06 — Prove it | M6 | |
| 07 — Deploy | M7 | |

| ADR | Decision |
|-----|----------|
| [0001](adr/0001-stack-dotnet-yarp.md) | .NET 10 + YARP + EF Core |
| [0002](adr/0002-token-bucket.md) | Token bucket as the production algorithm |
| [0003](adr/0003-api-keys-sha256-bearer.md) | Bearer API keys stored as SHA-256 hashes |
| [0004](adr/0004-redis-lua-server-clock.md) | Redis + one atomic Lua script + Redis's clock |
| [0005](adr/0005-per-route-failure-policy.md) | Payments fail open (with local fallback), refunds fail closed |
| [0006](adr/0006-deploy-k3s-actions-tailscale.md) | k3s via GitHub Actions + Tailscale + narrow sudo helper; tailnet only |
| [0007](adr/0007-one-fake-backend-image.md) | One fake-backend codebase run as two services |
