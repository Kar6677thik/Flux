using Flux.Gateway.Admin;
using Flux.Gateway.Auth;
using Flux.Gateway.Data;
using Flux.Gateway.Http;
using Flux.Gateway.Proxy;
using Flux.Gateway.Tenancy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
    builder.Logging.ClearProviders().AddJsonConsole(o => o.IncludeScopes = true);

// Routes and upstream clusters live in configuration ("ReverseProxy" section), not code, so the
// same image can point at Compose service names locally and at k8s Services on the server.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Fail fast (spec §12): invalid settings stop the gateway at startup, not on the first request.
builder.Services.AddOptions<ApiKeyOptions>().BindConfiguration(ApiKeyOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName)
    .Validate(o => o.IsValid, $"Admin:Token must be at least {AdminOptions.MinTokenLength} characters, or unset to disable the admin API.")
    .ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);

// By default minimal APIs throw on an unreadable body only in Development; elsewhere they answer
// with a bare 400. Throwing everywhere sends both through the exception handler's Problem Details.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

// A pooled factory rather than a scoped DbContext: the auth middleware is a singleton and only
// needs a context on a cache miss, so it borrows one from the pool for exactly that long.
builder.Services.AddPooledDbContextFactory<FluxDbContext>((sp, db) => db
    .UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required."))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApiKeyCache>();
builder.Services.AddSingleton<ApiKeyAuthenticator>();
builder.Services.AddSingleton<PlanCatalog>();
builder.Services.AddHostedService<PlanRefreshService>();

var app = builder.Build();

await DatabaseStartup.RunAsync(app);

// Order matters: this is the request lifecycle from spec §5.
app.UseExceptionHandler(error => error.Run(http =>
    // Minimal APIs report an unreadable body (malformed JSON, say) by throwing BadHttpRequestException.
    http.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException bad
        ? FluxProblems.WriteAsync(http, bad.StatusCode, "invalid_request", "Invalid request", "The request body could not be read as JSON.")
        : FluxProblems.WriteAsync(http, StatusCodes.Status500InternalServerError, "internal_error", "Internal error",
            "Something went wrong in the gateway. Quote the requestId when reporting it.")));
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<StripFluxHeadersMiddleware>();

// Liveness: is the process alive? Readiness: should it get traffic? Readiness waits for the plan
// catalog, and deliberately nothing else (spec §11.2).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", (PlanCatalog plans) => plans.IsLoaded
    ? Results.Ok(new { status = "ready" })
    : Results.Json(new { status = "not_ready", reason = "plans not loaded" }, statusCode: StatusCodes.Status503ServiceUnavailable));

// No token configured means no admin API at all: the routes don't exist, so they 404.
if (app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Enabled)
    app.MapAdmin();

// Only requests that matched a public route get here, so gateway endpoints never need a key.
app.MapReverseProxy(proxy =>
{
    proxy.UseMiddleware<ApiKeyAuthMiddleware>();
    proxy.UseMiddleware<UpstreamErrorMiddleware>();
});

// Anything that matched neither a gateway endpoint nor a proxy route.
app.MapFallback(http => FluxProblems.WriteAsync(http, StatusCodes.Status404NotFound, "route_not_found",
    "Route not found", $"No route matches {http.Request.Method} {http.Request.Path}."));

app.Run();

public partial class Program;
