namespace Flux.Gateway.Data;

// Persistence shapes for spec §10. They stay plain: the hot path never sees them, it sees the
// cached projections (AuthenticatedMerchant, PlanCatalog) instead.

public sealed class Plan
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public List<PlanRouteLimit> RouteLimits { get; set; } = [];
}

public sealed class PlanRouteLimit
{
    public required string PlanId { get; set; }
    public required string RouteGroup { get; set; }
    public decimal RefillPerSecond { get; set; }
    public int BurstCapacity { get; set; }
}

public enum MerchantStatus
{
    Active,
    Suspended,
}

public sealed class Merchant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string PlanId { get; set; }
    public MerchantStatus Status { get; set; } = MerchantStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public List<ApiKeyRecord> ApiKeys { get; set; } = [];
}

public sealed class ApiKeyRecord
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public required string KeyPrefix { get; set; }
    public required byte[] KeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
