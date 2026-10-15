using Flux.Gateway.Observability;
using Flux.Gateway.Tenancy;
using Yarp.ReverseProxy.Model;

namespace Flux.Gateway.Proxy;

/// <summary>
/// Step 3 of the request lifecycle (spec §5), the first thing in YARP's pipeline: YARP has matched
/// a route, and this reads which route group it belongs to. Everything after it, including the
/// metrics for a 401, can then say "payments" or "refunds".
/// </summary>
public sealed class RouteGroupMiddleware(RequestDelegate next)
{
    public const string MetadataKey = "RouteGroup";
    private const string ItemKey = "flux.routeGroup";

    public Task InvokeAsync(HttpContext http)
    {
        var route = http.Features.Get<IReverseProxyFeature>()?.Route.Config;
        // Every public route declares its group in appsettings.json. A missing one is a config bug,
        // and failing loudly beats silently sharing some default bucket.
        if (route?.Metadata?.TryGetValue(MetadataKey, out var group) != true || !RouteGroups.All.Contains(group!))
            throw new InvalidOperationException($"Route '{route?.RouteId}' has no valid '{MetadataKey}' metadata.");

        http.Items[ItemKey] = group;
        if (http.Telemetry() is { } telemetry)
            telemetry.RouteGroup = group;
        return next(http);
    }

    public static string GetRouteGroup(HttpContext http) =>
        http.Items[ItemKey] as string ?? throw new InvalidOperationException("RouteGroupMiddleware has not run.");
}
