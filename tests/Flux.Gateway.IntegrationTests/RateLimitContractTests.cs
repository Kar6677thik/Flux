using System.Net;
using System.Net.Http.Json;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// Spec §7.3 and §7.4 through the real gateway: every header, the 429 body, and which requests share
/// a bucket. The gateway's clock is a FakeTimeProvider, so refills happen only when a test says so.
/// </summary>
public sealed class RateLimitContractTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(GatewayFactory Gateway, HttpClient Client)> FreeMerchantAsync()
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address);
        _gateways.Add(gateway);
        var merchant = await gateway.CreateMerchantAsync("free", Ct);
        return (gateway, gateway.WithKey(merchant.ApiKey));
    }

    private static string Header(HttpResponseMessage response, string name) => Assert.Single(response.Headers.GetValues(name));

    [Fact]
    public async Task Allowed_responses_carry_limit_remaining_and_reset()
    {
        var (_, client) = await FreeMerchantAsync();

        var response = await client.GetAsync("/v1/payments/pay_1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("10", Header(response, "X-RateLimit-Limit"));
        Assert.Equal("9", Header(response, "X-RateLimit-Remaining"));
        Assert.Equal("1", Header(response, "X-RateLimit-Reset"));
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.False(response.Headers.Contains("X-RateLimit-Degraded"));
    }

    [Fact]
    public async Task The_eleventh_request_in_an_instant_is_429_with_retry_after()
    {
        var (_, client) = await FreeMerchantAsync();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);

        var limited = await client.GetAsync("/v1/payments/pay_1", Ct);

        var problem = await Problems.AssertAsync(limited, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        Assert.Equal(TimeSpan.FromSeconds(1), limited.Headers.RetryAfter?.Delta);
        Assert.Equal("0", Header(limited, "X-RateLimit-Remaining"));
        Assert.Equal("10", Header(limited, "X-RateLimit-Limit"));
        Assert.Equal("Merchant plan 'free' allows 10 requests/s on route group 'payments'.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_limited_request_is_never_forwarded()
    {
        var (_, client) = await FreeMerchantAsync();
        for (var i = 0; i < 2; i++) await client.PostAsJsonAsync("/v1/refunds", new { amount = 1 }, Ct);   // free refunds: burst 2
        var before = upstream.RequestCount;

        var limited = await client.PostAsJsonAsync("/v1/refunds", new { amount = 1 }, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(before, upstream.RequestCount);
    }

    [Fact]
    public async Task Tokens_refill_with_time()
    {
        var (gateway, client) = await FreeMerchantAsync();
        for (var i = 0; i < 10; i++) await client.GetAsync("/v1/payments/pay_1", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);

        gateway.Time.Advance(TimeSpan.FromMilliseconds(100));   // exactly one token at 10/s

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Get_and_post_share_one_bucket_per_route_group()
    {
        var (_, client) = await FreeMerchantAsync();
        for (var i = 0; i < 5; i++) await client.GetAsync("/v1/payments/pay_1", Ct);
        for (var i = 0; i < 5; i++) await client.PostAsJsonAsync("/v1/payments", new { amount = 1 }, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/refunds/rfnd_1", Ct)).StatusCode);   // other group untouched
    }

    [Fact]
    public async Task Limits_are_per_merchant_so_a_second_key_buys_nothing()
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address);
        _gateways.Add(gateway);
        var merchant = await gateway.CreateMerchantAsync("free", Ct);
        var second = await (await gateway.Admin().PostAsync($"/admin/merchants/{merchant.MerchantId}/keys", null, Ct))
            .Content.ReadFromJsonAsync<Admin.CreatedKeyResponse>(Ct);
        for (var i = 0; i < 10; i++) await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        // Same merchant, different key: same bucket, already empty.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await gateway.WithKey(second!.ApiKey).GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
        // A different merchant on the same plan is unaffected (noisy-neighbour isolation).
        var other = await gateway.CreateMerchantAsync("free", Ct);
        Assert.Equal(HttpStatusCode.OK, (await gateway.WithKey(other.ApiKey).GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Rejected_requests_spend_no_tokens()
    {
        var (gateway, client) = await FreeMerchantAsync();
        var anonymous = gateway.CreateClient();
        for (var i = 0; i < 20; i++) await anonymous.GetAsync("/v1/payments/pay_1", Ct);   // 401s: no merchant, no bucket

        Assert.Equal("9", Header(await client.GetAsync("/v1/payments/pay_1", Ct), "X-RateLimit-Remaining"));
    }

    [Fact]
    public async Task Gateway_endpoints_and_unknown_routes_have_no_rate_limit_headers()
    {
        var (gateway, client) = await FreeMerchantAsync();

        foreach (var path in new[] { "/healthz", "/readyz", "/v1/nope" })
            Assert.False((await client.GetAsync(path, Ct)).Headers.Contains("X-RateLimit-Limit"), path);
        Assert.False((await gateway.CreateClient().GetAsync("/v1/payments/pay_1", Ct)).Headers.Contains("X-RateLimit-Limit"));
    }

    [Fact]
    public async Task Config_selects_the_algorithm_without_any_other_change()
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address,
            new Dictionary<string, string?> { ["RateLimiting:Algorithm"] = "InMemoryFixedWindow" });
        _gateways.Add(gateway);
        var merchant = await gateway.CreateMerchantAsync("free", Ct);

        var response = await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        // Same middleware, same headers, different Strategy: the fixed window's quota is r × W = 100.
        Assert.Equal("100", Header(response, "X-RateLimit-Limit"));
        Assert.Equal("99", Header(response, "X-RateLimit-Remaining"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}
