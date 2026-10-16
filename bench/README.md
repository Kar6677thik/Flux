# Flux experiments (k6)

Every experiment in spec §15 has a script here and a report in [`docs/reports/`](../docs/reports/). k6 runs from its Docker image (`grafana/k6:2.3.0`, capped at 2 CPUs) on the Compose network, so it reaches the gateways by service name.

| Script | Experiment | Needs |
|--------|-----------|-------|
| [`k6/e1-instances.js`](k6/e1-instances.js) | E1 (in-memory) and P1 (`FLUX_ALGORITHM=RedisTokenBucket`): 3 gateways, one merchant at 100 req/s | `cluster` profile (baseline: `single`) |
| [`k6/e2-boundary.js`](k6/e2-boundary.js) | E2: the fixed window's boundary burst | `single` profile with `FLUX_ALGORITHM=InMemoryFixedWindow` |
| [`k6/p2-noisy-neighbour.js`](k6/p2-noisy-neighbour.js) | P2: a quiet merchant's latency with and without a flooding one (`NOISY_RATE`) | `single` profile, `FLUX_ALGORITHM=RedisTokenBucket` |
| [`k6/p3-overhead.js`](k6/p3-overhead.js) | P3: 500 req/s direct to the backend (`TARGET=direct`) or through the gateway, plus `Server-Timing` | `single` profile, `FLUX_ALGORITHM=RedisTokenBucket` |
| [`k6/redis-outage.js`](k6/redis-outage.js) via [`run-outage.sh`](run-outage.sh) | E3 and P4: steady traffic while Redis is stopped (`stop`) or frozen (`pause`) for ~20 s | `single` profile with `FLUX_ALGORITHM=RedisTokenBucket` (E3: also `FLUX_REDIS_RESILIENCE=false FLUX_REDIS_TIMEOUT_MS=5000`) |

`run-outage.sh <label> [stop|pause]` starts the k6 run, breaks Redis at +20 s, heals it about 20 s later, records both instants in `bench/results/<label>-events.txt`, then runs [`analyze_outage.py`](analyze_outage.py). That script uses only the standard library: it places each request in a phase by the moment it was *sent*, and prints the per-phase table and per-second timeline used in the reports.

## The README's visuals
- **`demo.sh`**: a ~30 s tour (auth, a 429 burst, Redis killed live, recovery) against a running `single` stack with `FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1`. It stops and starts the Compose Redis.
- **`record_demo.py`** records it as an asciicast (`docs/images/demo.cast`), then render it with `npx --yes svg-term-cli --in docs/images/demo.cast --out docs/images/demo.svg --window --width 100 --height 34`. svg-term is a one-off local tool with old dependencies; only its output is committed.
- **`make_charts.py`** (matplotlib) draws `docs/images/{limits,outage,outage-latency}-{light,dark}.svg` from the raw E3/P4 NDJSON in `bench/results/` and the E1/P1 report numbers.

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
