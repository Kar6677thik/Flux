using Flux.Gateway.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Flux.Gateway.Proxy;

/// <summary>
/// Runs inside YARP's proxy pipeline. When forwarding fails, YARP sets a status code (502 or 504)
/// but writes an empty body; this turns that into our Problem Details contract (spec §7.4).
/// </summary>
public sealed class UpstreamErrorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        await next(http);

        var error = http.GetForwarderErrorFeature();
        if (error is null || http.Response.HasStarted)
            return;

        if (error.Error == ForwarderError.RequestTimedOut)
        {
            await FluxProblems.WriteAsync(http, StatusCodes.Status504GatewayTimeout, "upstream_timeout",
                "Upstream timeout", "The backend service did not respond in time.");
        }
        else if (error.Error is not ForwarderError.RequestCanceled)   // client went away: nobody to answer
        {
            await FluxProblems.WriteAsync(http, StatusCodes.Status502BadGateway, "upstream_unavailable",
                "Upstream unavailable", "The backend service could not be reached.");
        }
    }
}
