using Flux.Gateway.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Flux.Gateway.IntegrationTests.RedisFixture))]

namespace Flux.Gateway.IntegrationTests;

/// <summary>One real Redis 7.4 for the test run (spec §14: never mock Redis). Tests isolate by merchant id.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>A connection exactly as a gateway would build it, so two of them act as two gateways.</summary>
    public static RedisConnection Connect(string connectionString, int timeoutMs = 1_000) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = connectionString }).Build(),
        Options.Create(new RateLimitingOptions { Redis = { TimeoutMs = timeoutMs } }),
        NullLogger<RedisConnection>.Instance);
}
