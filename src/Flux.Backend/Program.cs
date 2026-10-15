using Flux.Backend;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
    builder.Logging.ClearProviders().AddJsonConsole(o => o.IncludeScopes = true);

// Fail fast: a backend that doesn't know which service it is must not start (Field Guide §2.18).
builder.Services.AddOptions<BackendOptions>()
    .Bind(builder.Configuration.GetSection(BackendOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Same codebase, two services: each process only exposes its own endpoints (ADR-0007).
var serviceName = app.Services.GetRequiredService<IOptions<BackendOptions>>().Value.ServiceName;
if (serviceName == "payments")
    app.MapPayments();
else
    app.MapRefunds();

app.Run();

public partial class Program;
