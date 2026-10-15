namespace Flux.Gateway.Tenancy;

/// <summary>The units limits apply to (spec §6). Several public routes share one group.</summary>
public static class RouteGroups
{
    public const string Payments = "payments";
    public const string Refunds = "refunds";

    public static readonly IReadOnlyList<string> All = [Payments, Refunds];
}
