using System.Buffers;

namespace Flux.Gateway.Http;

/// <summary>
/// Step 1 of the request lifecycle (spec §5). Every request gets an id that appears in our logs,
/// on the upstream request and on the response, so one request can be traced across services.
/// A client-supplied id is kept only if it is short and uses safe characters: it ends up in logs
/// and headers, so arbitrary input there would be a log-injection risk.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    private const int MaxLength = 64;
    private static readonly SearchValues<char> Allowed =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    public async Task InvokeAsync(HttpContext http)
    {
        var incoming = http.Request.Headers[HeaderName].ToString();
        var requestId = IsValid(incoming) ? incoming : Guid.NewGuid().ToString("N");

        http.Items[HeaderName] = requestId;
        http.Request.Headers[HeaderName] = requestId;   // YARP copies request headers upstream
        http.Response.OnStarting(() =>
        {
            http.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["requestId"] = requestId }))
        {
            await next(http);
        }
    }

    internal static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength && !value.AsSpan().ContainsAnyExcept(Allowed);
}

public static class RequestIdExtensions
{
    public static string GetRequestId(this HttpContext http) =>
        http.Items[CorrelationIdMiddleware.HeaderName] as string ?? http.TraceIdentifier;
}
