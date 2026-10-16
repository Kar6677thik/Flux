# Flux experiments (k6)

Every experiment in spec §15 has a script here and a report in [`docs/reports/`](../docs/reports/). k6 runs from its Docker image (`grafana/k6:2.3.0`, capped at 2 CPUs) on the Compose network, so it reaches the gateways by service name.

| Script | Experiment | Needs |
|--------|-----------|-------|
| [`k6/e1-instances.js`](k6/e1-instances.js) | E1: 3 gateways with in-memory buckets → ~3× the limit | `cluster` profile (baseline: `single`) |
| [`k6/e2-boundary.js`](k6/e2-boundary.js) | E2: the fixed window's boundary burst | `single` profile with `FLUX_ALGORITHM=InMemoryFixedWindow` |

## Running one
```bash
docker compose -f deploy/compose/docker-compose.yml --profile cluster up --build -d
docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run --quiet /scripts/e1-instances.js
```
- **Git Bash on Windows:** prefix with `MSYS_NO_PATHCONV=1`, or `/scripts/e1-instances.js` is rewritten into a Windows path and k6 can't find it.
- **Fresh state:** in-memory buckets live in the gateway processes, so `docker compose … restart gateway-1 gateway-2 gateway-3` (or `gateway`) before each run.
- **Switching algorithm:** `FLUX_ALGORITHM=InMemoryFixedWindow docker compose … up -d gateway` recreates the gateway with the new setting.
- **Parameters** go in as `-e NAME=value`:
  - `BASE_URL`: where traffic goes (default `http://nginx:8080`);
  - `GATEWAYS`: which gateways to check readiness and `/metrics` on (default `gateway-1,gateway-2,gateway-3`);
  - `LABEL`: names the result file;
  - E1 also takes `RATE` and `SECONDS`; E2 takes `WINDOW_SECONDS`, `PER_BATCH`, `LEAD_MS` and `LAG_MS`.

Each run prints a short summary and writes the full k6 JSON to `bench/results/<LABEL>.json`. That folder is git-ignored; the numbers that matter are copied into the report along with the machine specs.

## Rules for every experiment (spec §15)
- **Constant arrival rate** for anything about rates or latency, never a closed loop: see [coordinated omission](../docs/concepts/coordinated-omission.md).
- Check `dropped_iterations`. Anything above 0 means k6 couldn't keep up, and the run didn't apply the load it claims.
- Record the machine, the image versions and the exact commands. Report measured numbers only.
