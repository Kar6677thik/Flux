namespace Flux.Backend;

/// <summary>
/// The backends trust the gateway: it authenticates the caller and passes the merchant id in
/// X-Flux-Merchant-Id. A request without that header did not come through the gateway, so it is
/// rejected. This is only safe because the backends are unreachable from outside the private network.
/// </summary>
public static class MerchantContext
{
    public const string MerchantHeader = "X-Flux-Merchant-Id";
    private const string ItemKey = "flux.merchantId";

    public static async ValueTask<object?> RequireMerchant(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var merchantId = http.Request.Headers[MerchantHeader].ToString();
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing merchant context",
                detail: $"Requests must arrive through the Flux gateway, which sets {MerchantHeader}.",
                extensions: new Dictionary<string, object?> { ["code"] = "missing_merchant_context" });
        }

        http.Items[ItemKey] = merchantId;
        return await next(ctx);
    }

    public static string MerchantId(this HttpContext http) => (string)http.Items[ItemKey]!;
}
