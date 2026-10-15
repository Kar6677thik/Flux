# Metrics and cardinality

> Field Guide: §24 Observability (RED metrics, histograms, cardinality), §27 Performance (percentiles) · Introduced in: M3
> Code: [`Observability/FluxMetrics.cs`](../../src/Flux.Gateway/Observability/FluxMetrics.cs), [`Observability/RequestTelemetry.cs`](../../src/Flux.Gateway/Observability/RequestTelemetry.cs), [`Proxy/UpstreamTimingMiddleware.cs`](../../src/Flux.Gateway/Proxy/UpstreamTimingMiddleware.cs) · Decision: [ADR-0009](../adr/0009-metrics-system-diagnostics-otel-prometheus.md)

## Problem
Logs answer "what happened to *this* request?". They're bad at "what fraction of requests were limited in the last 5 minutes?" or "what's our p99 overhead?": you'd have to scan every line. Metrics are pre-aggregated numbers that answer exactly those questions, cheaply, forever.

## Context
Prometheus scrapes `/metrics` every few seconds and stores each **time series**: one metric name plus one exact combination of label values. Grafana graphs them. Flux exposes metrics in M3; dashboards come in Project 5.

## Solution
| Metric (Prometheus name) | Type | Labels | Answers |
|--------------------------|------|--------|---------|
| `flux_requests_total` | counter | `route_group`, `outcome` | Rate and error mix: allowed / limited / unauthorized / forbidden / auth_unavailable / limiter_unavailable / not_found |
| `flux_gateway_overhead_seconds` | histogram | none | How much latency *the gateway itself* adds (spec N1) |
| `flux_ratelimiter_duration_seconds` | histogram | `algorithm` | Cost of one limiter decision. In memory: microseconds; Redis (M5): a network round trip |

That's the **RED** method (Rate, Errors, Duration) applied to a gateway.

### Cardinality: the one rule that matters
Each distinct combination of label values is a separate series, stored separately, forever.
```
flux_requests_total{route_group, outcome}
  = 2 route groups (+ "none") × 7 outcomes = at most 21 series        ✅

add merchant_id:
  × 10,000 merchants = 210,000 series, for one metric                 ❌
  Razorpay-scale: millions. Prometheus memory and query time explode.
```
That's why there is **no `merchant_id` label** (spec §11.2), and why `outcome` is a closed list of constants (`Outcomes`), never free text such as an exception message. Per-merchant questions are answered from **logs** (which carry `merchantId`), or in experiments by k6, which tags its own requests.

### Histograms, and why bucket boundaries matter
A histogram counts observations into buckets (`le="0.001"` = "at most 1 ms"), and Prometheus estimates percentiles from them. The estimate can't be finer than the buckets.
- OpenTelemetry's default boundaries are `0, 5, 10, 25 … 10000`, chosen for **milliseconds**.
- Our unit is **seconds** (the Prometheus convention), so with the defaults *every* gateway measurement (~0.0001 s) would fall in the first bucket. The p50 and the p99 would be indistinguishable.
- `FluxMetrics` therefore declares boundaries from 0.1 ms to 1 s with `InstrumentAdvice`. The test `Latency_histograms_are_exported_in_seconds_with_sub_millisecond_buckets` checks that `le="0.00025"` exists.

### Gateway overhead, and `Server-Timing`
```
Server-Timing: gw;dur=0.084, upstream;dur=6.016        ← measured in M3's Compose run (ms)
```
- `RequestTelemetryMiddleware` (outermost) starts the clock.
- `UpstreamTimingMiddleware` (innermost, just before YARP's forwarder) marks when we started waiting on the backend.
- Just before the response starts, the total is split: `upstream` = waiting on the backend until its response began; `gw` = everything else.
- The same `gw` value goes into the overhead histogram, and k6 can read the header per request in P3, which measures overhead a second, independent way (direct vs. through the gateway).

`Server-Timing` is a W3C standard header: browsers show it in DevTools' timing tab.

## Alternatives
- **Logs-based metrics** (count log lines in Loki or Elastic): flexible, but expensive at volume.
- **Tracing** (OpenTelemetry spans): shows *where* one request spent its time. Complements metrics; a Project 5 topic.
- **Summaries** (client-side percentiles): exact p99 per instance, but can't be aggregated across 3 instances. Histograms can, which is why we use them.

## Trade-offs
- **Overhead is measured up to the response start**, not through body streaming. For our small JSON bodies the difference is negligible.
- **Only decided requests are counted.** Health checks, scrapes and admin calls would drown the signal, so they set no outcome and aren't counted.
- **Upstream `502`/`504`s count as `allowed`**, because the limiter did allow them. Upstream health deserves its own metric later.

## Implementation
- Instruments come from `IMeterFactory` (one `Meter` per DI container), not a `static Meter`. Every test host gets its own instruments, which are disposed with the host.
- **Test gotcha, found in M3:** a `MeterListener` (what the OpenTelemetry SDK uses) is **process-wide**. In a test run with many gateways alive at once, each exporter also sees the others' meters, which share the name `Flux.Gateway`, so counts were inflated whenever other tests ran in parallel. The tests passed alone and failed in the full run. Tests that assert on counts now run in a non-parallel xUnit collection. In production there's one gateway per process, so nothing changes there.

## Testing
[`ObservabilityTests`](../../tests/Flux.Gateway.IntegrationTests/ObservabilityTests.cs) scrapes the real `/metrics`:
- exact counts per outcome and route group;
- the histogram names and bucket boundaries;
- **no merchant id anywhere** in the output;
- the `Server-Timing` format, with and without an upstream.

## Observability
Try it: `curl localhost:8080/metrics | grep ^flux_`. In M3's Compose run, after a burst from `noisy-corp`: `outcome="allowed"` 36, `outcome="limited"` 3, and an overhead sum of 0.084 s over 39 requests (≈ 2.2 ms each, including cold-start JIT on the first requests).

## Related
[token-bucket](token-bucket.md) · [middleware-pipeline](middleware-pipeline.md) · later: latency-percentiles (M6), benchmarking-methodology (M6) · ADR-0009
