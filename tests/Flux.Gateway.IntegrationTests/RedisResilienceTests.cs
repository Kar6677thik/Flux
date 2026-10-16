using System.Net;
using Flux.Gateway.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Testcontainers.Redis;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// The gateway on RedisTokenBucket, healthy and broken (spec §9.4–9.5, ADR-0005). "Broken" is
/// either a port nothing listens on, or a real Redis container stopped in the middle of a test.
/// </summary>
public sealed class RedisResilienceTests(EchoUpstream upstream, PostgresFixture postgres, RedisFixture redis)
    : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    private const string DeadRedis = "127.0.0.1:1";
    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayFactory Gateway(string redisConnection, bool resilience = true)
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address, new Dictionary<string, string?>
        {
            ["RateLimiting:Algorithm"] = "RedisTokenBucket",
            ["ConnectionStrings:Redis"] = redisConnection,
            ["RateLimiting:Redis:UseResilience"] = resilience ? "true" : "false",
        });
        _gateways.Add(gateway);
        return gateway;
    }

    private static string Header(HttpResponseMessage response, string name) => Assert.Single(response.Headers.GetValues(name));

    [Fact]
    public async Task With_redis_up_the_contract_is_unchanged()
    {
        var gateway = Gateway(redis.ConnectionString);
        var client = gateway.WithKey((await gateway.CreateMerchantAsync(PostgresFixture.SlowPlan, Ct)).ApiKey);

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
        var limited = await client.GetAsync("/v1/payments/pay_1", Ct);

        await Problems.AssertAsync(limited, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        Assert.Equal("10", Header(limited, "X-RateLimit-Limit"));
        Assert.False(limited.Headers.Contains("X-RateLimit-Degraded"));
    }

    [Fact]
    public async Task Two_gateways_share_one_bucket_which_is_the_fix_for_E1()
    {
        var a = Gateway(redis.ConnectionString);
        var b = Gateway(redis.ConnectionString);
        var merchant = await a.CreateMerchantAsync(PostgresFixture.SlowPlan, Ct);

        for (var i = 0; i < 5; i++) await a.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);
        for (var i = 0; i < 5; i++) await b.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        // In-memory buckets would allow 10 more on *each* gateway. Shared: the 11th is refused anywhere.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await b.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
    }

    [Fact]
    public async Task With_redis_down_the_gateway_still_starts_and_is_ready()
    {
        var response = await Gateway(DeadRedis).CreateClient().GetAsync("/readyz", Ct);

        // Readiness never depends on Redis (spec §11.2): otherwise an outage would pull every gateway.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task With_redis_down_payments_fail_open_to_a_local_share_of_the_limit()
    {
        var gateway = Gateway(DeadRedis);
        var client = gateway.WithKey((await gateway.CreateMerchantAsync("free", Ct)).ApiKey);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await client.GetAsync("/v1/payments/pay_1", Ct);
            statuses.Add(last.StatusCode);
        }

        // free payments: B = 10 → ceil(10 / 3 instances) = 4 locally. "Open" is not "unlimited".
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal("true", Header(last, "X-RateLimit-Degraded"));
        Assert.Equal("4", Header(last, "X-RateLimit-Limit"));
    }

    [Fact]
    public async Task With_redis_down_refunds_fail_closed_with_503()
    {
        var gateway = Gateway(DeadRedis);
        var client = gateway.WithKey((await gateway.CreateMerchantAsync("free", Ct)).ApiKey);

        var response = await client.GetAsync("/v1/refunds/rfnd_1", Ct);

        await Problems.AssertAsync(response, HttpStatusCode.ServiceUnavailable, "rate_limiter_unavailable");
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task The_breaker_opens_after_20_failures_and_retries_after_5_seconds()
    {
        var gateway = Gateway(DeadRedis);
        var client = gateway.WithKey((await gateway.CreateMerchantAsync("pro", Ct)).ApiKey);
        var limiter = Assert.IsType<ResilientRateLimiter>(gateway.Services.GetRequiredService<IRateLimiter>());

        for (var i = 0; i < ResilientRateLimiter.MinimumThroughput - 1; i++) await client.GetAsync("/v1/payments/pay_1", Ct);
        Assert.Equal(CircuitState.Closed, limiter.CircuitState);   // too few calls to judge yet

        await client.GetAsync("/v1/payments/pay_1", Ct);
        Assert.Equal(CircuitState.Open, limiter.CircuitState);     // 20 of 20 failed

        // While open, requests are still answered (degraded) without touching Redis at all.
        Assert.Equal("true", Header(await client.GetAsync("/v1/payments/pay_1", Ct), "X-RateLimit-Degraded"));

        Assert.Equal(0, limiter.TrialCalls);

        // After the break, the next request is let through to Redis as a trial. Redis is still
        // dead, so the trial fails and the breaker opens again for another 5 s.
        gateway.Time.Advance(ResilientRateLimiter.BreakDuration);
        await client.GetAsync("/v1/payments/pay_1", Ct);
        Assert.Equal(1, limiter.TrialCalls);
        Assert.Equal(CircuitState.Open, limiter.CircuitState);
    }

    [Fact]
    public async Task Without_resilience_a_redis_failure_is_a_500_which_is_what_E3_shows()
    {
        var gateway = Gateway(DeadRedis, resilience: false);
        var client = gateway.WithKey((await gateway.CreateMerchantAsync("free", Ct)).ApiKey);

        await Problems.AssertAsync(await client.GetAsync("/v1/payments/pay_1", Ct), HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task Stopping_redis_mid_test_degrades_payments_and_closes_refunds()
    {
        await using var ownRedis = new RedisBuilder("redis:7.4-alpine").Build();
        await ownRedis.StartAsync(Ct);
        var gateway = Gateway(ownRedis.GetConnectionString());
        var client = gateway.WithKey((await gateway.CreateMerchantAsync("pro", Ct)).ApiKey);
        var healthy = await client.GetAsync("/v1/payments/pay_1", Ct);
        Assert.False(healthy.Headers.Contains("X-RateLimit-Degraded"));

        await ownRedis.StopAsync(Ct);

        var payments = await client.GetAsync("/v1/payments/pay_1", Ct);
        var refunds = await client.GetAsync("/v1/refunds/rfnd_1", Ct);
        Assert.Equal(HttpStatusCode.OK, payments.StatusCode);
        Assert.Equal("true", Header(payments, "X-RateLimit-Degraded"));
        await Problems.AssertAsync(refunds, HttpStatusCode.ServiceUnavailable, "rate_limiter_unavailable");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}
