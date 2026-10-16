using System.Globalization;
using Flux.Gateway.Auth;
using Flux.Gateway.Http;
using Flux.Gateway.Observability;
using Flux.Gateway.Proxy;
using Flux.Gateway.Tenancy;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// Step 5 of the request lifecycle (spec §5), right after auth inside YARP's pipeline. A limited
/// request is answered here and never forwarded: protecting the backend is the whole point.
/// The token is spent at admission, so a request the backend later fails still counts.
/// </summary>
public sealed class RateLimitMiddleware(
    RequestDelegate next, IRateLimiter limiter, PlanCatalog plans, FluxMetrics metrics, TimeProvider time, ILogger<RateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var merchant = http.GetMerchant()
            ?? throw new InvalidOperationException("RateLimitMiddleware must run after ApiKeyAuthMiddleware.");
        var routeGroup = RouteGroupMiddleware.GetRouteGroup(http);

        // Until the catalog has loaded, the gateway is unready and shouldn't be getting traffic at
        // all. If it gets some anyway, refusing is safer than guessing a limit.
        if (!plans.TryGet(merchant.PlanId, out var plan) || !plan.Routes.TryGetValue(routeGroup, out var limit))
        {
            logger.LogWarning("No limit known for plan {planId} on {routeGroup}", merchant.PlanId, routeGroup);
            http.SetOutcome(Outcomes.LimiterUnavailable);
            http.Response.Headers.RetryAfter = "5";
            await FluxProblems.WriteAsync(http, StatusCodes.Status503ServiceUnavailable, "rate_limiter_unavailable",
                "Rate limiter unavailable", "Limits could not be determined right now. Retry shortly.");
            return;
        }

        var started = time.GetTimestamp();
        var decision = await limiter.TryAcquireAsync(new RateLimitKey(merchant.MerchantId, routeGroup), limit, http.RequestAborted);
        metrics.LimiterDecision(limiter.Algorithm, time.GetElapsedTime(started));

        // OnStarting, not "set now": the headers must land on the backend's response too, and YARP
        // copies the backend's headers over ours when it forwards. This runs after that copy.
        http.Response.OnStarting(() =>
        {
            RateLimitHeaders.Apply(http.Response.Headers, decision);
            return Task.CompletedTask;
        });

        if (!decision.Allowed)
        {
            http.SetOutcome(Outcomes.Limited);
            var rate = limit.RefillPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
            await FluxProblems.WriteAsync(http, StatusCodes.Status429TooManyRequests, "rate_limit_exceeded",
                "Rate limit exceeded", $"Merchant plan '{plan.Id}' allows {rate} requests/s on route group '{routeGroup}'.");
            return;
        }

        http.SetOutcome(Outcomes.Allowed);
        await next(http);
    }
}
