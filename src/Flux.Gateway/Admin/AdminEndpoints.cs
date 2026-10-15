using Flux.Gateway.Auth;
using Flux.Gateway.Data;
using Flux.Gateway.Http;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.Admin;

public sealed record CreateMerchantRequest(string? Name, string? Plan);
public sealed record UpdateMerchantRequest(string? Plan, string? Status);
public sealed record CreatedMerchantResponse(Guid MerchantId, string ApiKey, string KeyPrefix);
public sealed record CreatedKeyResponse(Guid KeyId, string ApiKey, string KeyPrefix);
public sealed record KeyView(Guid KeyId, string KeyPrefix, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record MerchantView(Guid MerchantId, string Name, string Plan, string Status, DateTimeOffset CreatedAt, IReadOnlyList<KeyView> Keys);

/// <summary>
/// The admin API (spec §11.1). Every change here reaches the hot path only through the caches, so
/// plan changes, suspensions and revocations take up to the cache TTL (60 s) to apply everywhere.
/// </summary>
public static class AdminEndpoints
{
    private const int MaxNameLength = 100;

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminTokenFilter>();

        admin.MapPost("/merchants", CreateMerchant);
        admin.MapGet("/merchants/{id:guid}", GetMerchant);
        admin.MapPatch("/merchants/{id:guid}", UpdateMerchant);
        admin.MapPost("/merchants/{id:guid}/keys", CreateKey);
        admin.MapDelete("/keys/{keyId:guid}", RevokeKey);
    }

    private static async Task<IResult> CreateMerchant(
        CreateMerchantRequest body, IDbContextFactory<FluxDbContext> dbFactory, TimeProvider time, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return FluxProblem.InvalidRequest($"'name' is required and must be at most {MaxNameLength} characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (body.Plan is null || !await db.Plans.AnyAsync(p => p.Id == body.Plan, ct))
            return FluxProblem.InvalidRequest($"'plan' must be one of: {string.Join(", ", await db.Plans.Select(p => p.Id).ToListAsync(ct))}.");

        var now = time.GetUtcNow();
        var merchant = new Merchant { Id = Guid.CreateVersion7(now), Name = name, PlanId = body.Plan, CreatedAt = now };
        var (record, apiKey) = NewKey(merchant.Id, now);
        merchant.ApiKeys.Add(record);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(ct);   // one transaction: never a merchant without its first key

        return Results.Created($"/admin/merchants/{merchant.Id}", new CreatedMerchantResponse(merchant.Id, apiKey, record.KeyPrefix));
    }

    private static async Task<IResult> GetMerchant(Guid id, IDbContextFactory<FluxDbContext> dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var merchant = await db.Merchants.AsNoTracking().Include(m => m.ApiKeys).FirstOrDefaultAsync(m => m.Id == id, ct);
        return merchant is null ? MerchantNotFound(id) : Results.Ok(ToView(merchant));
    }

    private static async Task<IResult> UpdateMerchant(
        Guid id, UpdateMerchantRequest body, IDbContextFactory<FluxDbContext> dbFactory, CancellationToken ct)
    {
        if (body.Plan is null && body.Status is null)
            return FluxProblem.InvalidRequest("Provide 'plan', 'status' or both.");

        // Matched by name, not Enum.TryParse, which would also accept "1" or "Active, Suspended".
        MerchantStatus? status = body.Status switch
        {
            "active" => MerchantStatus.Active,
            "suspended" => MerchantStatus.Suspended,
            _ => null,
        };
        if (body.Status is not null && status is null)
            return FluxProblem.InvalidRequest("'status' must be 'active' or 'suspended'.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var merchant = await db.Merchants.Include(m => m.ApiKeys).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (merchant is null)
            return MerchantNotFound(id);
        if (body.Plan is not null && !await db.Plans.AnyAsync(p => p.Id == body.Plan, ct))
            return FluxProblem.InvalidRequest($"Plan '{body.Plan}' does not exist.");

        merchant.PlanId = body.Plan ?? merchant.PlanId;
        merchant.Status = status ?? merchant.Status;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(merchant));
    }

    private static async Task<IResult> CreateKey(
        Guid id, IDbContextFactory<FluxDbContext> dbFactory, TimeProvider time, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Merchants.AnyAsync(m => m.Id == id, ct))
            return MerchantNotFound(id);

        var (record, apiKey) = NewKey(id, time.GetUtcNow());
        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/merchants/{id}", new CreatedKeyResponse(record.Id, apiKey, record.KeyPrefix));
    }

    private static async Task<IResult> RevokeKey(
        Guid keyId, IDbContextFactory<FluxDbContext> dbFactory, TimeProvider time, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key is null)
            return new FluxProblem(StatusCodes.Status404NotFound, "key_not_found", "Key not found", $"No API key has id {keyId}.");

        // Idempotent: revoking twice is still 204, and the original revocation time is kept.
        key.RevokedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // The plaintext key exists only in this method's return value and the one HTTP response that
    // carries it. Only the hash and the display prefix are stored.
    private static (ApiKeyRecord Record, string ApiKey) NewKey(Guid merchantId, DateTimeOffset now)
    {
        var apiKey = ApiKey.Generate();
        var record = new ApiKeyRecord
        {
            Id = Guid.CreateVersion7(now),
            MerchantId = merchantId,
            KeyPrefix = ApiKey.PrefixOf(apiKey),
            KeyHash = ApiKey.Hash(apiKey),
            CreatedAt = now,
        };
        return (record, apiKey);
    }

    private static MerchantView ToView(Merchant m) => new(
        m.Id, m.Name, m.PlanId, m.Status.ToString().ToLowerInvariant(), m.CreatedAt,
        m.ApiKeys.OrderBy(k => k.CreatedAt).Select(k => new KeyView(k.Id, k.KeyPrefix, k.CreatedAt, k.RevokedAt)).ToList());

    private static FluxProblem MerchantNotFound(Guid id) =>
        new(StatusCodes.Status404NotFound, "merchant_not_found", "Merchant not found", $"No merchant has id {id}.");
}
