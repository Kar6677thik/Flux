# Load testing with k6

> Field Guide: §23.14 Load Testing, §23.15 Stress Testing, §27 Performance engineering · Introduced in: M4
> Code: [`bench/k6/`](../../bench/k6/), [`bench/README.md`](../../bench/README.md) · Reports: [E1](../reports/E1-per-instance-buckets.md), [E2](../reports/E2-fixed-window-boundary.md)

## Problem
Unit and integration tests prove *logic* with a frozen clock. They can't show what happens with real arrival times, real concurrency, three processes and a load balancer. M3's hand-run `curl` "burst" let 14 through instead of 10, because starting 25 processes took longer than the burst was meant to last. Experiments need a tool that generates load precisely, records what happened, and can be rerun identically.

## Context
k6 (Grafana) runs JavaScript test scripts on a Go engine: one binary, no JVM, scriptable, with good executors for arrival-rate load. Alternatives include Gatling, Locust, wrk/wrk2 and Vegeta. Flux runs k6 from its Docker image, so nothing needs installing, and pins the version (`2.3.0`).

## Solution
A k6 script has a lifecycle:
```js
export const options = { scenarios: { … } };   // how much load, in what shape
export function setup() { waitUntilReady(…) }   // once, before the load
export default function () { http.get(…) }      // one iteration = one unit of load
export function teardown() { … }                // once, after (E1 scrapes /metrics here)
export function handleSummary(data) { … }       // write the results the way you want them
```

### Executors: the shape of the load
| Executor | Shape | Used for |
|----------|-------|----------|
| `constant-arrival-rate` | Start N iterations per second, however long each one takes | **E1** (100 req/s), and every latency experiment in M5–M6 |
| `per-vu-iterations` | Each VU runs exactly K iterations | **E2** (one precisely timed iteration) |
| `constant-vus` / `ramping-vus` | N virtual users looping as fast as responses allow | Avoided: a closed loop (see [coordinated-omission](coordinated-omission.md)) |

### Things that bit or nearly bit us
- **`http.batch` defaults:** at most 20 parallel requests per batch and **6 per host**. E2's "100 at once" would really have been 6 at a time without `batch: 100, batchPerHost: 100`.
- **`dropped_iterations`:** if k6 runs out of VUs it *skips* scheduled iterations and counts them here. Every E1 run reports it; all were 0.
- **Custom metrics** (`Counter`, `Trend`) count what *we* care about (allowed vs. limited, the burst span), not just HTTP timings. Metrics added in `teardown` still reach the summary, which is how E1 includes each gateway's own count.
- **Git Bash path mangling:** `/scripts/e1.js` became `C:/Program Files/Git/scripts/…` until we set `MSYS_NO_PATHCONV=1`.
- **Shared CPUs:** k6 runs on the same laptop as the system under test. It's capped at 2 CPUs in Compose, and the cap is recorded in every report.

## Alternatives
| Tool | Trade-off |
|------|-----------|
| wrk2 | Tiny, very precise constant-rate HTTP load (it was built to fix coordinated omission in wrk). Scripting in Lua; less convenient for multi-step logic like E2's timed batches. |
| Vegeta | A constant-rate Go CLI; good for single-endpoint latency, minimal scripting. |
| Gatling | A powerful Scala/Java DSL, heavier to run. |
| Locust | Python, closed-loop by default. You must be careful about the coordinated-omission trap. |
| `curl` in a loop | Fine for a smoke test. M3 showed it's useless for timing. |

## Trade-offs
- **JavaScript, but not Node.** No npm modules; k6 has its own runtime. Helpers live in `bench/k6/lib/flux.js`.
- **Containerised k6 measures through Docker networking.** Comparisons *within* an experiment are valid; absolute numbers include the virtual machine's overhead (spec §15).

## Implementation
- `lib/flux.js` holds the dev keys, `waitUntilReady` (polls each gateway's `/readyz`, so no run starts against a gateway still loading plans), and `requestCounts` (parses `flux_requests_total` from `/metrics`).
- Results: a short `stdout` summary for the terminal and the full JSON in `bench/results/<label>.json` (git-ignored). The reports quote the numbers.

## Testing
Scripts are tested by their results agreeing with independent sources. In E1, k6's allowed count equals the sum of the three gateways' own metrics (1,822 = 608 + 607 + 607).

## Observability
k6 output plus the gateway's `/metrics` and `Server-Timing`. P3 (M6) will use `Server-Timing` as a second, independent overhead measurement.

## Related
[coordinated-omission](coordinated-omission.md) · [load-balancing](load-balancing.md) · [fixed-and-sliding-windows](fixed-and-sliding-windows.md) · later: latency-percentiles, benchmarking-methodology (M6)
