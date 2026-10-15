using System.Data.Common;
using Flux.Gateway.Data;
using Flux.Gateway.Http;
using Microsoft.Net.Http.Headers;

namespace Flux.Gateway.Auth;

/// <summary>
/// Step 4 of the request lifecycle (spec §5). It runs inside YARP's pipeline, so it only sees
/// requests that matched a public route: gateway endpoints never need a key, and an unknown path is
/// a 404 before anyone asks for credentials.
/// </summary>
public sealed class ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyAuthenticator authenticator, ILogger<ApiKeyAuthMiddleware> logger)
{
    public const string MerchantHeader = "X-Flux-Merchant-Id";
    public const string PlanHeader = "X-Flux-Plan";
    private const string BearerScheme = "Bearer ";

    public async Task InvokeAsync(HttpContext http)
    {
        if (!TryReadKey(http.Request, out var key))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"flux\"";
            await FluxProblems.WriteAsync(http, StatusCodes.Status401Unauthorized, "missing_api_key",
                "Missing API key", "Send your key as 'Authorization: Bearer flux_...'.");
            return;
        }

        AuthenticatedMerchant? merchant;
        try
        {
            merchant = await authenticator.FindAsync(key, http.RequestAborted);
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            // ADR-0008: keys already in the cache keep working; only cache misses land here.
            logger.LogError(ex, "Key lookup failed for {keyPrefix}: database unavailable", ApiKey.PrefixOf(key));
            http.Response.Headers.RetryAfter = "5";
            await FluxProblems.WriteAsync(http, StatusCodes.Status503ServiceUnavailable, "auth_unavailable",
                "Authentication unavailable", "The key could not be checked right now. Retry shortly.");
            return;
        }

        if (merchant is null)
        {
            logger.LogInformation("Rejected unknown or revoked key {keyPrefix}", ApiKey.PrefixOf(key));
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"flux\", error=\"invalid_token\"";
            await FluxProblems.WriteAsync(http, StatusCodes.Status401Unauthorized, "invalid_api_key",
                "Invalid API key", "The API key is unknown or has been revoked.");
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["merchantId"] = merchant.MerchantId,
            ["keyPrefix"] = merchant.KeyPrefix,
        });

        // Authentication said who you are; this is authorization deciding whether you may proceed.
        if (merchant.Status == MerchantStatus.Suspended)
        {
            logger.LogInformation("Rejected suspended merchant");
            await FluxProblems.WriteAsync(http, StatusCodes.Status403Forbidden, "merchant_suspended",
                "Merchant suspended", "This merchant account is suspended.");
            return;
        }

        http.SetMerchant(merchant);
        var headers = http.Request.Headers;
        headers[MerchantHeader] = merchant.MerchantId.ToString();
        headers[PlanHeader] = merchant.PlanId;
        // The backends trust X-Flux-Merchant-Id and have no use for the secret itself. Every extra
        // place a key travels is another place it can leak (logs, traces, crash dumps).
        headers.Remove(HeaderNames.Authorization);

        await next(http);
    }

    // EF Core wraps errors it considers transient (a refused connection, say) in an
    // InvalidOperationException, so the DbException can be one level down.
    private static bool IsDatabaseUnavailable(Exception ex) =>
        ex is DbException or TimeoutException || ex is InvalidOperationException { InnerException: DbException };

    private static bool TryReadKey(HttpRequest request, out string key)
    {
        key = "";
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value)
            return false;

        // RFC 9110: the auth scheme is case-insensitive ("bearer" is as valid as "Bearer").
        if (!value.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = value.AsSpan(BearerScheme.Length).Trim();
        if (!ApiKey.IsWellFormed(candidate))
            return false;

        key = candidate.ToString();
        return true;
    }
}
