using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flux.Gateway.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// A real gateway (the actual Program.cs) wired to the test PostgreSQL, an upstream of the test's
/// choosing and a fake clock. Each test that needs different settings builds its own.
/// </summary>
public sealed class GatewayFactory(
    string postgres, string upstream, IReadOnlyDictionary<string, string?>? settings = null, string environment = "Development")
    : WebApplicationFactory<Program>
{
    public const string AdminToken = "test-admin-token-0123456789abcdef";

    // Starts at a fixed instant so test output is reproducible; only Advance() moves it.
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var all = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres,
            ["Database:MigrateOnStartup"] = "false",   // PostgresFixture already did both
            ["Database:SeedDevData"] = "false",
            ["Admin:Token"] = AdminToken,
            ["ReverseProxy:Clusters:payments:Destinations:d1:Address"] = upstream,
            ["ReverseProxy:Clusters:refunds:Destinations:d1:Address"] = upstream,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            all[key] = value;
        foreach (var (key, value) in all)
            builder.UseSetting(key, value);

        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AdminTokenFilter.HeaderName, AdminToken);
        return client;
    }

    public HttpClient WithKey(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <summary>A fresh merchant per test, so tests that revoke or suspend can't disturb each other.</summary>
    public async Task<CreatedMerchantResponse> CreateMerchantAsync(string plan = "free", CancellationToken ct = default)
    {
        var response = await Admin().PostAsJsonAsync("/admin/merchants", new { name = $"test-{Guid.NewGuid():N}", plan }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedMerchantResponse>(ct))!;
    }
}
