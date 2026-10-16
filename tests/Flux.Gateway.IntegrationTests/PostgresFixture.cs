using Flux.Gateway.Data;
using Flux.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Flux.Gateway.IntegrationTests.PostgresFixture))]

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// One real PostgreSQL 17 container for the whole test run (spec §14: never mock the database).
/// It's migrated and dev-seeded once, here, rather than by each gateway the tests start: dozens of
/// hosts migrating one database in parallel would only test the migration lock.
/// Tests stay independent by creating their own merchants instead of sharing mutable rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        await DevSeed.ApplyAsync(db, new FakeTimeProvider(), CancellationToken.None);

        // Redis's clock can't be faked, so tests that count admissions against Redis use a plan
        // that refills so slowly that real time passing during the test can't add a token.
        db.Plans.Add(new Plan
        {
            Id = SlowPlan, DisplayName = "Tests: slow refill",
            RouteLimits =
            [
                new PlanRouteLimit { PlanId = SlowPlan, RouteGroup = RouteGroups.Payments, RefillPerSecond = 0.001m, BurstCapacity = 10 },
                new PlanRouteLimit { PlanId = SlowPlan, RouteGroup = RouteGroups.Refunds, RefillPerSecond = 0.001m, BurstCapacity = 2 },
            ],
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Burst 10 / 2 like "free", but refilling at 0.001/s: one token every ~17 minutes.</summary>
    public const string SlowPlan = "test-slow";

    public FluxDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxDbContext>()
        .UseNpgsql(ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
