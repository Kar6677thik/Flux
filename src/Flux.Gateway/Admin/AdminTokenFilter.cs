using System.Security.Cryptography;
using System.Text;
using Flux.Gateway.Http;
using Microsoft.Extensions.Options;

namespace Flux.Gateway.Admin;

/// <summary>
/// Guards every /admin endpoint (spec §11.1). Unlike API keys, the admin token is compared directly,
/// so the comparison must take the same time whether the first character or the last one is wrong.
/// Otherwise response timing would reveal the token one character at a time.
/// </summary>
public sealed class AdminTokenFilter(IOptions<AdminOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Token";

    // Hashing both sides first gives two inputs of equal length, so FixedTimeEquals doesn't return
    // early on a length mismatch, and the token's length isn't leaked either.
    private readonly byte[] _expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.Token ?? ""));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.HttpContext.Request.Headers[HeaderName].ToString()));
        if (!CryptographicOperations.FixedTimeEquals(given, _expected))
        {
            return new FluxProblem(StatusCodes.Status401Unauthorized, "invalid_admin_token",
                "Invalid admin token", $"Admin endpoints require a valid {HeaderName} header.");
        }

        return await next(ctx);
    }
}
