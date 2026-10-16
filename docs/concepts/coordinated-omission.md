# Coordinated omission

> Field Guide: §27 Performance engineering (percentiles, measurement), §23.14 Load Testing · Introduced in: M4
> Code: every k6 scenario in [`bench/k6/`](../../bench/k6/) · Term coined by Gil Tene (author of HdrHistogram and wrk2)

## Problem
Most simple load generators work in a **closed loop**: send a request, wait for the answer, send the next. When the server stalls, the generator stalls *with* it and stops sending. The slow period then contains **few samples** (the generator was waiting), so it barely shows in the percentiles. The tool has quietly "coordinated" with the server to omit exactly the measurements that would have been bad.

## Context
It affects any latency claim: "p99 = 12 ms", Flux's N1 target (≤ 2 ms p50 / ≤ 10 ms p99 overhead), and E3's question of what latency does when Redis dies. It's also why "requests per second achieved" from a closed-loop tool says more about the tool than the server.

## Solution
**Open-loop (constant-arrival-rate) load:** requests are *scheduled* at a fixed rate, independent of responses. If the server stalls for 1 s at 100 req/s, 100 requests pile up, each experiencing part of that second, and all of them are measured.

```
closed loop, 1 VU, server stalls for 1 s:
  t=0.00 send → 0.01 ok → send → … → t=0.50 send ─── stall 1 s ───▶ 1.50 ok → send …
  samples: ~50 fast ones, then ONE slow one → p99 looks fine

open loop, 100 req/s, same stall:
  a request starts every 10 ms, regardless → ~100 requests caught in the stall
  samples: ~100 slow ones, waiting 0–1000 ms → p99 shows the stall
```
In k6: `executor: 'constant-arrival-rate'` with enough `maxVUs` that it never has to wait, and check that `dropped_iterations` is 0. A non-zero count means k6 itself became the closed loop.

## Alternatives
- **Correcting after the fact** (HdrHistogram's "expected interval" correction) back-fills the missing samples. It's useful when you can't change the tool, but it's an estimate.
- **Measuring in production** from real arrivals (server-side histograms) avoids the problem for *server* time, but not for "what the client experienced".

## Trade-offs
- An open-loop generator can **overwhelm** a server that can't keep up: the queue grows without bound. That's the honest answer ("at this rate, it falls over"), but it needs a timeout and a sensible `maxVUs`.
- It needs **more VUs** during slow periods (in flight = rate × latency). E1 allowed up to 200.

## Implementation
- E1 uses `constant-arrival-rate` at 100 req/s. Its result is a *count*, which coordinated omission would distort less than latency, but the habit matters for M5 and M6.
- E2 uses `per-vu-iterations` deliberately. It isn't measuring a rate at all, just firing two precisely timed batches.
- M3's lesson in miniature: 25 sequential `curl`s were a closed loop. Each one waited for the previous answer, and the "burst" stretched over hundreds of milliseconds.

## Testing
Every report lists `dropped_iterations`. For E1 it was 0 in all five runs.

## Observability
Server-side histograms (`flux_gateway_overhead_seconds`) measure from arrival, so they're immune to the generator's behaviour. Comparing them with k6's client-side numbers is part of P3's methodology.

## Related
[load-testing-with-k6](load-testing-with-k6.md) · [metrics-and-cardinality](metrics-and-cardinality.md) · later: latency-percentiles, benchmarking-methodology (M6)
