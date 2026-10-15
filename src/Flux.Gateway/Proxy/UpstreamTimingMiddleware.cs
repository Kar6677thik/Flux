using Flux.Gateway.Observability;

namespace Flux.Gateway.Proxy;

/// <summary>
/// The innermost step in YARP's pipeline, right before the forwarder: it marks when we started
/// waiting on the backend. On success the response starts while forwarding is still in progress,
/// so Server-Timing measures up to the backend's first byte; on a failure (502/504) the clock
/// stops here, before the error body is written.
/// </summary>
public sealed class UpstreamTimingMiddleware(RequestDelegate next, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var telemetry = http.Telemetry();
        if (telemetry is not null)
            telemetry.UpstreamStart = time.GetTimestamp();

        await next(http);

        if (telemetry is not null)
            telemetry.UpstreamEnd = time.GetTimestamp();
    }
}
