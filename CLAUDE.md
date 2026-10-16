# Working on Flux

Flux is a **learning project**. The owner learns from the docs as much as from the code.

## Source of truth
- `docs/spec.md` is the agreed specification. To change a decision, add a new ADR in `docs/adr/` that supersedes the old one. Don't silently edit an accepted ADR.
- Work happens in milestones M1–M7 (spec §19). Each milestone gets its own branch `m<N>-<slug>` and a PR into `main`. Never push or merge without the owner's OK.
- Commits and PRs are authored by the owner only: no `Co-Authored-By` trailers, no "Generated with Claude Code" footers.

## Documentation is part of "done"
Every milestone must add or update:
- `docs/journal/NN-<slug>.md`, in the same shape as `00-specification.md`: what we did, why, why now, concepts with Field Guide § links, commands to verify, exercises with answers, what could go wrong, what's next;
- a `docs/concepts/<name>.md` note for each new concept (use `_template.md`);
- the ADRs for any new decision with real alternatives;
- `docs/glossary.md`, for new terms;
- `docs/reports/<id>.md`, for every experiment: hypothesis, setup, machine specs, exact commands, numbers, conclusion. Report measured numbers only.

Code comments explain *why*, not *what*. Keep them sparse; the depth belongs in the docs.

## Home server (Megatron, `ssh kar_thik@100.91.115.46`)
- **Flux is not deployed there** ([ADR-0011](docs/adr/0011-no-home-server-deploy-publish-images.md)); M7 became a showcase (GHCR images + recorded demo). The rules below still apply to any inspection.
- **Read-only inspection only.** No destructive commands. No manual deployment: deploys go through GitHub Actions only.
- k3s runs there and also hosts Slate. Don't touch the `slate` namespace or Tailscale Serve's `/` mapping.

## Conventions
- .NET 10, nullable enabled, warnings as errors.
- Tests: xUnit + Testcontainers. Never mock PostgreSQL or Redis. Never sleep on the wall clock; use `FakeTimeProvider`.
- Never log full API keys; log `key_prefix` only. No secrets in the repo (it is public).

## Commands
- Build: `dotnet build -c Release` (warnings fail the build). Test: `dotnet test` (xUnit v3 on Microsoft.Testing.Platform, opted in via `global.json`).
- Run locally: `dotnet run --project src/Flux.Backend --launch-profile payments|refunds` (ports 5201/5202) and `dotnet run --project src/Flux.Gateway` (port 5100).
- Containers: `docker compose -f deploy/compose/docker-compose.yml --profile single up --build` (gateway on :8080, PostgreSQL on 127.0.0.1:5433).
- Three gateways behind nginx: `--profile cluster` (same :8080; `down` the other profile first). Experiments: `MSYS_NO_PATHCONV=1 docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet /scripts/<script>.js`; see `bench/README.md`. Switch the algorithm with `FLUX_ALGORITHM=<name>` on `up`.
- Redis (M5) is in Compose on 127.0.0.1:6380. Outage experiments: `bench/run-outage.sh <label> [stop|pause]` against a gateway started with `FLUX_ALGORITHM=RedisTokenBucket`.
- The gateway needs PostgreSQL. For `dotnet run`, start just the database: `docker compose -f deploy/compose/docker-compose.yml up -d postgres`.
- Run from published images (no SDK): add `-f deploy/compose/docker-compose.ghcr.yml` to any compose command. CI pushes `ghcr.io/kar6677thik/flux-{gateway,backend}:{sha-<commit>,latest}` on main.
- README visuals: `python -I bench/make_charts.py` (needs bench/results from E3/P4) and `bench/demo.sh` → `bench/record_demo.py` → svg-term (see bench/README.md).
- Migrations: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/Flux.Gateway --output-dir Data/Migrations`. Read the generated SQL (`dotnet ef migrations script`) before committing.
