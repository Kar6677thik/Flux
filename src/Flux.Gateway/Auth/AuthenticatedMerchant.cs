using Flux.Gateway.Data;

namespace Flux.Gateway.Auth;

/// <summary>What auth hands to the rest of the pipeline: who is calling, on which plan.</summary>
public sealed record AuthenticatedMerchant(Guid MerchantId, string PlanId, MerchantStatus Status, string KeyPrefix);

public static class AuthenticatedMerchantExtensions
{
    private const string ItemKey = "flux.merchant";

    public static void SetMerchant(this HttpContext http, AuthenticatedMerchant merchant) => http.Items[ItemKey] = merchant;

    public static AuthenticatedMerchant? GetMerchant(this HttpContext http) => http.Items[ItemKey] as AuthenticatedMerchant;
}
