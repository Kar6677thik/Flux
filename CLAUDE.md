# Working on Flux

Flux is a **learning project**. The owner learns from the docs as much as from the code.

## Source of truth
- `docs/spec.md` is the agreed specification. To change a decision, add a new ADR in `docs/adr/` that supersedes the old one. Don't silently edit an accepted ADR.
- Work happens in milestones M1–M7 (spec §19). Each milestone gets its own branch `m<N>-<slug>` and a PR into `main`. Never push or merge without the owner's OK.

## Documentation is part of "done"
Every milestone must add or update:
- `docs/journal/NN-<slug>.md`, in the same shape as `00-specification.md`: what we did, why, why now, concepts with Field Guide § links, commands to verify, exercises with answers, what could go wrong, what's next;
- a `docs/concepts/<name>.md` note for each new concept (use `_template.md`);
- the ADRs for any new decision with real alternatives;
- `docs/glossary.md`, for new terms;
- `docs/reports/<id>.md`, for every experiment: hypothesis, setup, machine specs, exact commands, numbers, conclusion. Report measured numbers only.

Code comments explain *why*, not *what*. Keep them sparse; the depth belongs in the docs.

## Home server (Megatron, `ssh kar_thik@100.91.115.46`)
- **Read-only inspection only.** No destructive commands. No manual deployment: deploys go through GitHub Actions only.
- k3s runs there and also hosts Slate. Don't touch the `slate` namespace or Tailscale Serve's `/` mapping.

## Conventions
- .NET 10, nullable enabled, warnings as errors.
- Tests: xUnit + Testcontainers. Never mock PostgreSQL or Redis. Never sleep on the wall clock; use `FakeTimeProvider`.
- Never log full API keys; log `key_prefix` only. No secrets in the repo (it is public).
