using System.Net;
using System.Net.Http.Json;
using Flux.Gateway.Data;
using Flux.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flux.Gateway.IntegrationTests;

/// <summary>Fail-fast configuration, readiness, the seeded plans and the schema's own guards.</summary>
public sealed class StartupTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>
{
    private const string DeadPostgres = "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readiness_is_503_until_plans_load_and_200_after()
    {
        await using var down = new GatewayFactory(DeadPostgres, upstream.Address);
        var notReady = await down.CreateClient().GetAsync("/readyz", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await down.CreateClient().GetAsync("/healthz", Ct)).StatusCode);   // alive, just not ready

        await using var up = new GatewayFactory(postgres.ConnectionString, upstream.Address);
        var client = up.CreateClient();
        await up.Services.GetRequiredService<PlanCatalog>().Loaded.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readyz", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_migration_seeds_the_plans_from_the_spec()
    {
        await using var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address);
        gateway.CreateClient();
        var catalog = gateway.Services.GetRequiredService<PlanCatalog>();
        await catalog.Loaded.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(catalog.TryGet("free", out var free));
        Assert.True(catalog.TryGet("pro", out var pro));
        Assert.Equal(new RouteLimit(10, 10), free.Routes[RouteGroups.Payments]);
        Assert.Equal(new RouteLimit(2, 2), free.Routes[RouteGroups.Refunds]);
        Assert.Equal(new RouteLimit(100, 100), pro.Routes[RouteGroups.Payments]);
        Assert.Equal(new RouteLimit(20, 20), pro.Routes[RouteGroups.Refunds]);
    }

    [Fact]
    public async Task The_seeded_dev_merchants_authenticate_with_their_plans()
    {
        await using var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address);

        var quiet = await gateway.WithKey(DevSeed.QuietShop.ApiKey).GetFromJsonAsync<EchoedRequest>("/v1/payments/pay_1", Ct);
        var noisy = await gateway.WithKey(DevSeed.NoisyCorp.ApiKey).GetFromJsonAsync<EchoedRequest>("/v1/payments/pay_1", Ct);

        Assert.Equal("pro", quiet!.Header("X-Flux-Plan"));
        Assert.Equal("free", noisy!.Header("X-Flux-Plan"));
    }

    [Fact]
    public async Task Dev_seeding_outside_development_refuses_to_start()
    {
        await using var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address,
            new Dictionary<string, string?> { ["Database:SeedDevData"] = "true" }, environment: "Production");

        var error = Assert.ThrowsAny<Exception>(() => gateway.CreateClient());
        Assert.Contains("SeedDevData", error.Message);
    }

    [Fact]
    public async Task A_short_admin_token_refuses_to_start()
    {
        await using var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address,
            new Dictionary<string, string?> { ["Admin:Token"] = "too-short" });

        var error = Assert.ThrowsAny<Exception>(() => gateway.CreateClient());
        Assert.Contains("Admin:Token", error.Message);
    }

    [Fact]
    public async Task The_database_itself_rejects_an_invalid_merchant_status()
    {
        await using var db = postgres.CreateDbContext();

        // Bypass EF's enum mapping with raw SQL: the CHECK constraint is the last line of defence.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "insert into merchants (id, name, plan_id, status) values (gen_random_uuid(), 'x', 'free', 'deleted')", Ct));
        Assert.Equal("ck_merchants_status", error.ConstraintName);
    }
}
