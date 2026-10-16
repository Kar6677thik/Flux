# ADR-0009: Metrics via System.Diagnostics.Metrics, exported by OpenTelemetry's Prometheus exporter

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M3 (closes the open item in spec §22)

## Context
Spec §11.2 asks for Prometheus-format metrics on `/metrics`: a request counter, two latency histograms, and later the limiter's fallback and circuit state. Spec §22 left the library open between "OpenTelemetry Prometheus exporter" and `prometheus-net`. Project 5 will add dashboards and probably OTLP export, so what we choose now should still make sense then.

The state of the packages on 2026-10-15:
- **`OpenTelemetry.Exporter.Prometheus.AspNetCore` is still a beta** (`1.19.1-beta.1`), even though the rest of OpenTelemetry .NET is stable (`1.19.1`).
- **`prometheus-net`** is stable (`8.2.1`), but its last release was in January 2024.

## Decision
Two layers, chosen separately:
1. **Instrumentation: `System.Diagnostics.Metrics`** (`Meter`, `Counter<T>`, `Histogram<T>`), created through `IMeterFactory`. This is .NET's built-in, vendor-neutral metrics API, which ASP.NET Core and the runtime use themselves. All of Flux's metric code is written against it ([`FluxMetrics`](../../src/Flux.Gateway/Observability/FluxMetrics.cs)).
2. **Export: OpenTelemetry's Prometheus exporter**, pinned to exactly `1.19.1-beta.1`, mapped at `/metrics`.

Naming follows OpenTelemetry conventions, and the exporter translates them: `flux.requests` → `flux_requests_total`, and `flux.gateway.overhead` with unit `s` → `flux_gateway_overhead_seconds`. Histograms declare their own bucket boundaries through `InstrumentAdvice`, because OpenTelemetry's defaults (0, 5, 10, 25 … 10000) assume milliseconds.

## Alternatives considered
| Option | Why not |
|--------|---------|
| `prometheus-net` for both layers | Stable and simple, but metric code would be written against a Prometheus-specific API, and moving to OTLP in Project 5 would mean rewriting every instrument. |
| `System.Diagnostics.Metrics` + `prometheus-net`'s meter adapter | Avoids the beta, but puts a second translation layer between our instruments and the output, maintained by a project that has slowed down. |
| OpenTelemetry + OTLP exporter only (no `/metrics`) | Needs a collector running before anything is visible. The spec wants a plain `/metrics` you can `curl`. |
| Hand-written Prometheus text | Easy for counters, fiddly and error-prone for histograms. Not where the learning is. |

## Consequences
- **A beta dependency in production code.** Mitigations: an exact version pin, tests that scrape `/metrics` and check the names and labels we rely on (`ObservabilityTests`), and an export layer that's a single line in `Program.cs`, so replacing it never touches the instruments.
- Every series carries an extra `otel_scope_name="Flux.Gateway"` label added by the exporter. It's harmless, since there's one value per meter.
- **Test gotcha:** a `MeterListener` is process-wide, so in a test run that hosts several gateways at once, each exporter also sees the other gateways' meters. The tests that assert on counts run in a non-parallel xUnit collection.

## Revisit when
- The Prometheus exporter reaches a stable release → drop the beta pin.
- Project 5 moves to OTLP and a collector → swap the exporter. `FluxMetrics` stays as it is.
