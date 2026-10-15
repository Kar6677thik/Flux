using Flux.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.Auth;

/// <summary>key → SHA-256 → cache → (on a miss) PostgreSQL → cache the answer, found or not (spec §8).</summary>
public sealed class ApiKeyAuthenticator(ApiKeyCache cache, IDbContextFactory<FluxDbContext> dbFactory)
{
    public async ValueTask<AuthenticatedMerchant?> FindAsync(string key, CancellationToken ct)
    {
        var hash = ApiKey.Hash(key);
        var cacheKey = Convert.ToHexString(hash);
        if (cache.TryGet(cacheKey, out var cached))
            return cached;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // A revoked key is treated exactly like an unknown one: the caller learns nothing either way.
        var merchant = await (
                from k in db.ApiKeys
                where k.KeyHash == hash && k.RevokedAt == null
                join m in db.Merchants on k.MerchantId equals m.Id
                select new AuthenticatedMerchant(m.Id, m.PlanId, m.Status, k.KeyPrefix))
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (merchant is null)
            cache.SetNotFound(cacheKey);
        else
            cache.SetFound(cacheKey, merchant);
        return merchant;
    }
}
