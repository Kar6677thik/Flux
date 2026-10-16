# Latency percentiles

> Field Guide: §27 Performance engineering (percentiles, tail latency), §24 Observability (histograms) · Introduced in: M6 (used since M4)
> Code: k6 `summaryTrendStats` in [`bench/k6/`](../../bench/k6/), `flux_gateway_overhead_seconds` in [`FluxMetrics`](../../src/Flux.Gateway/Observability/FluxMetrics.cs) · Reports: [P2](../reports/P2-noisy-neighbour.md), [P3](../reports/P3-gateway-overhead.md)

## Problem
"Average latency 8 ms" hides the requests that matter. Ten requests at 1 ms and one at 80 ms average 8.2 ms, and that 80 ms customer is the one who complains. Spec targets are written as percentiles (N1: p50 ≤ 2 ms, p99 ≤ 10 ms; N3: p95) for that reason.

## Context
**pN** is the latency that N% of requests are at or under. p50 is the median (the typical request), and p99 means 1 request in 100 was slower than this. In systems where one user action fans out to many calls, tail latency dominates the user's experience: a page making 100 backend calls has a 63% chance (1 − 0.99¹⁰⁰) of hitting at least one p99-slow call.

## Solution: read them correctly
| Read it as | Example from P3 |
|------------|-----------------|
| p50: what a typical request sees | gateway path 8.0 ms vs. direct 7.3–7.5 ms |
| p95 / p99: the tail; what you design for | gateway p99 11.0 ms |
| max: one sample, usually noise | direct max 34.9–36.1 ms, *without* any gateway involved |

### Three traps, all met in Flux
1. **The difference of percentiles is not the percentile of differences.** P3's "method 1" subtracts the direct path's p99 from the gateway path's p99. That's a fair estimate of the overhead's size, but not the p99 *of the overhead*: the slowest gateway requests and the slowest direct requests aren't the same requests. That's why P3 also reads `Server-Timing` per request (method 2) and reports both.
2. **Percentiles can't be averaged.** The p99 of three gateways is not the mean of their p99s. Prometheus histograms are aggregated by **summing bucket counts** first, then estimating the percentile, which is why spec §11.2 uses histograms rather than summaries.
3. **The estimate is only as fine as the buckets.** OpenTelemetry's default buckets assume milliseconds, so in seconds every request would land in the first bucket. M3 set buckets from 0.1 ms upward (ADR-0009). P3's limiter histogram resolved "49% ≤ 0.25 ms, 89% ≤ 0.5 ms" because of that.

## Alternatives
- **Mean ± standard deviation:** fine for symmetric distributions. Latency distributions are skewed with long right tails, so it misleads.
- **Full latency histograms or HDR heatmaps:** the most information, harder to put in one table. k6's NDJSON output (used in E3/P4) is the raw material for those.

## Trade-offs
- **p99 needs samples.** With 1,200 requests (P2's quiet merchant), p99 rests on the slowest 12. It's useful, but noisy. P3's 30,000 per run give a much steadier p99.
- **Tails include the machine.** Scheduling, power management and GC all show up in p99/max. Comparing *within* one experiment, on the same machine, under the same conditions, is what makes the numbers meaningful ([benchmarking-methodology](benchmarking-methodology.md)).

## Implementation
k6 reports `p(50)`, `p(95)`, `p(99)` and `max` per Trend (`summaryTrendStats`). The outage analysis script computes the same percentiles itself from NDJSON, per phase and per second.

## Testing
`Latency_histograms_are_exported_in_seconds_with_sub_millisecond_buckets` pins the histogram boundaries.

## Observability
In Prometheus: `histogram_quantile(0.99, sum by (le) (rate(flux_gateway_overhead_seconds_bucket[5m])))`. Note the `sum by (le)`: it aggregates instances *before* computing the quantile.

## Related
[coordinated-omission](coordinated-omission.md) (percentiles from a closed loop are wrong) · [metrics-and-cardinality](metrics-and-cardinality.md) · [benchmarking-methodology](benchmarking-methodology.md)
