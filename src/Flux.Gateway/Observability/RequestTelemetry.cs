using System.Globalization;

namespace Flux.Gateway.Observability;

/// <summary>
/// Per-request facts the pipeline fills in as it goes (route group, outcome, when forwarding began
/// and when the upstream answered), read back once to write Server-Timing and the metrics.
/// </summary>
public sealed class RequestTelemetry
{
    public long Start { get; init; }
    public string? RouteGroup { get; set; }
    public string? Outcome { get; set; }
    public long? UpstreamStart { get; set; }
    public long? UpstreamEnd { get; set; }
}

public static class RequestTelemetryExtensions
{
    public static RequestTelemetry? Telemetry(this HttpContext http) => http.Features.Get<RequestTelemetry>();

    public static void SetOutcome(this HttpContext http, string outcome)
    {
        if (http.Telemetry() is { } telemetry)
            telemetry.Outcome = outcome;
    }
}

/// <summary>
/// Outermost timing step. It starts the clock, and just before the response starts it splits the
/// elapsed time into "gateway" and "upstream" (spec §11.2): `Server-Timing: gw;dur=0.84, upstream;dur=5.21`.
/// That header lets k6 record gateway overhead per request in experiment P3.
/// </summary>
public sealed class RequestTelemetryMiddleware(RequestDelegate next, FluxMetrics metrics, TimeProvider time)
{
    public const string ServerTimingHeader = "Server-Timing";

    public async Task InvokeAsync(HttpContext http)
    {
        var telemetry = new RequestTelemetry { Start = time.GetTimestamp() };
        http.Features.Set(telemetry);

        http.Response.OnStarting(() =>
        {
            var now = time.GetTimestamp();
            var total = time.GetElapsedTime(telemetry.Start, now);
            var upstream = telemetry.UpstreamStart is { } started
                ? time.GetElapsedTime(started, telemetry.UpstreamEnd ?? now)
                : TimeSpan.Zero;
            var gateway = total - upstream;

            http.Response.Headers[ServerTimingHeader] = telemetry.UpstreamStart is null
                ? $"gw;dur={Ms(gateway)}"
                : $"gw;dur={Ms(gateway)}, upstream;dur={Ms(upstream)}";
            if (telemetry.Outcome is not null)
                metrics.GatewayOverhead(gateway);
            return Task.CompletedTask;
        });

        await next(http);

        // Only requests that reached a decision are counted. Health checks, scrapes of /metrics
        // and admin calls would drown the signal.
        if (telemetry.Outcome is { } outcome)
            metrics.RequestCompleted(telemetry.RouteGroup ?? "none", outcome);
    }

    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
}
