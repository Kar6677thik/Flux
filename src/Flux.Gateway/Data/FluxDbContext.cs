using Flux.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.Data;

/// <summary>
/// The schema from spec §10. Column names come out snake_case (UseSnakeCaseNamingConvention, set
/// where the context is registered), and the CHECK constraints live here too, so the database
/// rejects bad rows even if a future code path forgets to validate. Every foreign key is RESTRICT:
/// EF's default is CASCADE, under which deleting a plan would silently delete its merchants.
/// </summary>
public sealed class FluxDbContext(DbContextOptions<FluxDbContext> options) : DbContext(options)
{
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanRouteLimit> PlanRouteLimits => Set<PlanRouteLimit>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Plan>(plan =>
        {
            plan.ToTable("plans");
            plan.HasKey(p => p.Id);
            plan.HasMany(p => p.RouteLimits).WithOne().HasForeignKey(l => l.PlanId).OnDelete(DeleteBehavior.Restrict);
            plan.HasData(
                new Plan { Id = "free", DisplayName = "Free" },
                new Plan { Id = "pro", DisplayName = "Pro" });
        });

        model.Entity<PlanRouteLimit>(limit =>
        {
            limit.ToTable("plan_route_limits", t =>
            {
                t.HasCheckConstraint("ck_plan_route_limits_route_group", "route_group in ('payments', 'refunds')");
                t.HasCheckConstraint("ck_plan_route_limits_refill", "refill_per_second > 0");
                t.HasCheckConstraint("ck_plan_route_limits_burst", "burst_capacity > 0");
            });
            limit.HasKey(l => new { l.PlanId, l.RouteGroup });
            limit.Property(l => l.RefillPerSecond).HasPrecision(10, 3);
            // Spec §6: plans and limits exist in every environment, so they ship in a migration.
            limit.HasData(
                new PlanRouteLimit { PlanId = "free", RouteGroup = RouteGroups.Payments, RefillPerSecond = 10, BurstCapacity = 10 },
                new PlanRouteLimit { PlanId = "free", RouteGroup = RouteGroups.Refunds, RefillPerSecond = 2, BurstCapacity = 2 },
                new PlanRouteLimit { PlanId = "pro", RouteGroup = RouteGroups.Payments, RefillPerSecond = 100, BurstCapacity = 100 },
                new PlanRouteLimit { PlanId = "pro", RouteGroup = RouteGroups.Refunds, RefillPerSecond = 20, BurstCapacity = 20 });
        });

        model.Entity<Merchant>(merchant =>
        {
            merchant.ToTable("merchants", t =>
                t.HasCheckConstraint("ck_merchants_status", "status in ('active', 'suspended')"));
            merchant.HasKey(m => m.Id);
            merchant.Property(m => m.Id).ValueGeneratedNever();
            merchant.HasOne<Plan>().WithMany().HasForeignKey(m => m.PlanId).OnDelete(DeleteBehavior.Restrict);
            // Stored as lowercase text rather than an int: readable in psql, and the CHECK above
            // documents every allowed value in the schema itself.
            merchant.Property(m => m.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(), s => Enum.Parse<MerchantStatus>(s, true))
                .HasDefaultValueSql("'active'");
            merchant.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            merchant.HasMany(m => m.ApiKeys).WithOne().HasForeignKey(k => k.MerchantId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ApiKeyRecord>(key =>
        {
            key.ToTable("api_keys");
            key.HasKey(k => k.Id);
            key.Property(k => k.Id).ValueGeneratedNever();
            // The unique index is also the lookup path: auth finds a key by hash, never by
            // comparing secrets character by character.
            key.HasIndex(k => k.KeyHash).IsUnique();
            key.HasIndex(k => k.MerchantId).HasDatabaseName("ix_api_keys_merchant");
            key.Property(k => k.CreatedAt).HasDefaultValueSql("now()");
        });
    }
}
