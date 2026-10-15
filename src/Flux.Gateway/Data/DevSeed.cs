using Flux.Gateway.Auth;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.Data;

/// <summary>
/// Demo merchants with fixed keys for local work and the experiments (spec §10). These keys are in a
/// public repo, so they are public: <see cref="DatabaseStartup"/> refuses to seed them anywhere but
/// Development. The "dev" in each key makes a leaked one easy to recognise.
/// </summary>
public static class DevSeed
{
    public sealed record DemoMerchant(Guid Id, string Name, string PlanId, MerchantStatus Status, Guid KeyId, string ApiKey);

    public static readonly DemoMerchant QuietShop = new(
        Guid.Parse("8d8688a8-84d7-45d7-9f4a-b4708d86bed0"), "quiet-shop", "pro", MerchantStatus.Active,
        Guid.Parse("5b0f3c9e-6a51-4c8e-9d43-0e2f7a1b6c01"), "flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT");

    public static readonly DemoMerchant NoisyCorp = new(
        Guid.Parse("ae304a5f-2221-49df-92b3-4124df15fbbc"), "noisy-corp", "free", MerchantStatus.Active,
        Guid.Parse("5b0f3c9e-6a51-4c8e-9d43-0e2f7a1b6c02"), "flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM");

    public static readonly DemoMerchant SuspendedCo = new(
        Guid.Parse("1bf8ee7a-b7a7-430a-97fd-38a50fbacf53"), "suspended-co", "free", MerchantStatus.Suspended,
        Guid.Parse("5b0f3c9e-6a51-4c8e-9d43-0e2f7a1b6c03"), "flux_devSuspendedV3E3JkLSVkbisS2kfKQ5yn9BkiG9nic");

    public static readonly IReadOnlyList<DemoMerchant> All = [QuietShop, NoisyCorp, SuspendedCo];

    /// <summary>Idempotent: inserts whichever demo merchants are missing and leaves existing rows alone.</summary>
    public static async Task ApplyAsync(FluxDbContext db, TimeProvider time, CancellationToken ct)
    {
        var ids = All.Select(m => m.Id).ToList();
        var existing = await db.Merchants.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct);
        var now = time.GetUtcNow();

        foreach (var demo in All.Where(m => !existing.Contains(m.Id)))
        {
            db.Merchants.Add(new Merchant
            {
                Id = demo.Id,
                Name = demo.Name,
                PlanId = demo.PlanId,
                Status = demo.Status,
                CreatedAt = now,
                ApiKeys =
                [
                    new ApiKeyRecord
                    {
                        Id = demo.KeyId,
                        MerchantId = demo.Id,
                        KeyPrefix = ApiKey.PrefixOf(demo.ApiKey),
                        KeyHash = ApiKey.Hash(demo.ApiKey),
                        CreatedAt = now,
                    },
                ],
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
