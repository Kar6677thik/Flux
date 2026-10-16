using System.Diagnostics.Metrics;

namespace Flux.Gateway.Observability;

/// <summary>
/// The gateway's metrics (spec §11.2), written against .NET's built-in System.Diagnostics.Metrics
/// API. The Prometheus exporter is only plumbing (ADR-0009): swapping it never touches these.
/// Labels are deliberately low-cardinality. There is no merchant_id, because every distinct label
/// value is a separate time series.
/// </summary>
public sealed class FluxMetrics
{
    public const string MeterName = "Flux.Gateway";

    // The OpenTelemetry default buckets (0, 5, 10, 25 … 10000) assume milliseconds. In seconds
    // they'd put every request in the first bucket, so we advise sub-millisecond resolution.
    private static readonly IReadOnlyList<double> LatencyBuckets =
        [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1];

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _overhead;
    private readonly Histogram<double> _limiterDuration;

    public FluxMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        var advice = new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets };

        _requests = meter.CreateCounter<long>("flux.requests",
            description: "Requests that reached a decision, by route group and outcome.");
        _overhead = meter.CreateHistogram("flux.gateway.overhead", unit: "s",
            description: "Time spent in the gateway itself: until the response starts, minus waiting on the upstream.", advice: advice);
        _limiterDuration = meter.CreateHistogram("flux.ratelimiter.duration", unit: "s",
            description: "Time taken by one rate-limiter decision.", advice: advice);
    }

    public void RequestCompleted(string routeGroup, string outcome) =>
        _requests.Add(1, new("route_group", routeGroup), new("outcome", outcome));

    public void GatewayOverhead(TimeSpan duration) => _overhead.Record(duration.TotalSeconds);

    public void LimiterDecision(string algorithm, TimeSpan duration) =>
        _limiterDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("algorithm", algorithm));
}

/// <summary>The fixed set of outcome label values: a closed list, so the series count stays bounded.</summary>
public static class Outcomes
{
    public const string Allowed = "allowed";
    public const string Limited = "limited";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string LimiterUnavailable = "limiter_unavailable";
    public const string AuthUnavailable = "auth_unavailable";
    public const string NotFound = "not_found";
}
