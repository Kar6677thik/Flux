using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// A real HTTP server on a random local port that answers every request with what it received
/// (method, path, headers). Tests use it to see exactly what the gateway forwarded upstream.
/// YARP needs a real socket to talk to, which is why this is not an in-memory TestServer.
/// </summary>
public sealed class EchoUpstream : IAsyncLifetime
{
    private WebApplication? _app;

    public string Address { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        _app.MapGet("/payments/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Results.Ok();
        });
        // MapFallback, not app.Run: a terminal app.Run would execute before endpoint routing
        // and swallow the /payments/slow endpoint above.
        _app.MapFallback(async http =>
        {
            var echo = new EchoedRequest(
                http.Request.Method,
                http.Request.Path.Value ?? "",
                http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
            await http.Response.WriteAsJsonAsync(echo);
        });

        await _app.StartAsync();
        Address = _app.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}

public sealed record EchoedRequest(string Method, string Path, Dictionary<string, string> Headers)
{
    // HTTP header names are case-insensitive; a dictionary rebuilt from JSON is not.
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
