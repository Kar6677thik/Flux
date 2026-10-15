using Flux.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.Tenancy;

/// <summary>One route group's limit: the two numbers a token bucket needs (spec §9.2).</summary>
public sealed record RouteLimit(double RefillPerSecond, int BurstCapacity);

public sealed record PlanLimits(string Id, string DisplayName, IReadOnlyDictionary<string, RouteLimit> Routes);

/// <summary>
/// Every plan and its limits, held in memory (spec §8) so the hot path never queries for them.
/// Plans are a handful of rows that change rarely, so the whole set is swapped at once on refresh:
/// readers always see one consistent snapshot and need no lock.
/// </summary>
public sealed class PlanCatalog(IDbContextFactory<FluxDbContext> dbFactory)
{
    private volatile IReadOnlyDictionary<string, PlanLimits>? _plans;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Readiness (spec §11.2): no traffic until the plans have loaded at least once.</summary>
    public bool IsLoaded => _plans is not null;

    public Task Loaded => _loaded.Task;

    public bool TryGet(string planId, out PlanLimits plan)
    {
        plan = null!;
        return _plans?.TryGetValue(planId, out plan!) ?? false;
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var plans = await db.Plans.AsNoTracking().Include(p => p.RouteLimits).ToListAsync(ct);

        _plans = plans.ToDictionary(
            p => p.Id,
            p => new PlanLimits(p.Id, p.DisplayName, p.RouteLimits.ToDictionary(
                l => l.RouteGroup,
                l => new RouteLimit((double)l.RefillPerSecond, l.BurstCapacity))));
        _loaded.TrySetResult();
    }
}
