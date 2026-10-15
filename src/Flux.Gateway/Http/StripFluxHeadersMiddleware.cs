namespace Flux.Gateway.Http;

/// <summary>
/// Step 2 of the request lifecycle (spec §5). Backends trust X-Flux-* headers because only the
/// gateway is supposed to set them. If a client could send its own X-Flux-Merchant-Id, it could act
/// as any merchant, so every inbound X-Flux-* header is removed before anything else reads it.
/// </summary>
public sealed class StripFluxHeadersMiddleware(RequestDelegate next)
{
    public const string Prefix = "X-Flux-";

    public Task InvokeAsync(HttpContext http)
    {
        var headers = http.Request.Headers;
        foreach (var name in headers.Keys.Where(k => k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            headers.Remove(name);

        return next(http);
    }
}
