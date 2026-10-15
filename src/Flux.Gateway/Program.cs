using Flux.Gateway.Http;
using Flux.Gateway.Proxy;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
    builder.Logging.ClearProviders().AddJsonConsole(o => o.IncludeScopes = true);

// Routes and upstream clusters live in configuration ("ReverseProxy" section), not code, so the
// same image can point at Compose service names locally and at k8s Services on the server.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Order matters: this is the request lifecycle from spec §5. Auth (M2) and rate limiting (M3)
// slot in between header stripping and the proxy.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<StripFluxHeadersMiddleware>();

// Liveness: is the process alive? Readiness: should it get traffic? In M1 the gateway has no
// dependencies yet, so both are trivially true. M2 makes readiness wait for the plan cache.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.MapReverseProxy(proxy =>
{
    proxy.UseMiddleware<UpstreamErrorMiddleware>();
});

// Anything that matched neither a gateway endpoint nor a proxy route.
app.MapFallback(http => FluxProblems.WriteAsync(http, StatusCodes.Status404NotFound, "route_not_found",
    "Route not found", $"No route matches {http.Request.Method} {http.Request.Path}."));

app.Run();

public partial class Program;
