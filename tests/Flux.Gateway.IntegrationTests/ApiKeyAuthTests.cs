using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flux.Gateway.Auth;
using Flux.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace Flux.Gateway.IntegrationTests;

/// <summary>Spec §5 step 4 and §8: the 401/403 contract, what gets forwarded, and the cache TTLs.</summary>
public sealed class ApiKeyAuthTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayFactory NewGateway(string? postgresOverride = null)
    {
        var gateway = new GatewayFactory(postgresOverride ?? postgres.ConnectionString, upstream.Address);
        _gateways.Add(gateway);
        return gateway;
    }

    private static HttpRequestMessage Get(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_1");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    [Fact]
    public async Task No_authorization_header_is_401_missing_api_key()
    {
        var response = await NewGateway().CreateClient().SendAsync(Get(null), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "missing_api_key");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Theory]
    [InlineData("Basic Zmx1eDpzZWNyZXQ=")]                                    // wrong scheme
    [InlineData("Bearer")]                                                   // no key
    [InlineData("Bearer not-a-flux-key")]                                    // wrong format
    [InlineData("flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT")]          // key without the scheme
    public async Task A_malformed_authorization_header_is_401_missing_api_key(string authorization)
    {
        var response = await NewGateway().CreateClient().SendAsync(Get(authorization), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "missing_api_key");
    }

    [Fact]
    public async Task A_well_formed_but_unknown_key_is_401_invalid_api_key()
    {
        var response = await NewGateway().CreateClient().SendAsync(Get($"Bearer {ApiKey.Generate()}"), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid_api_key");
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.Single().Parameter);
    }

    [Fact]
    public async Task A_valid_key_is_forwarded_as_merchant_headers_and_the_key_itself_is_not()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync("free", Ct);

        var response = await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<EchoedRequest>(Ct);
        Assert.Equal(merchant.MerchantId.ToString(), echo!.Header("X-Flux-Merchant-Id"));
        Assert.Equal("free", echo.Header("X-Flux-Plan"));
        Assert.Null(echo.Header("Authorization"));
    }

    [Fact]
    public async Task The_bearer_scheme_is_case_insensitive()
    {
        var response = await NewGateway().CreateClient().SendAsync(Get($"bearer {DevSeed.QuietShop.ApiKey}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_suspended_merchant_is_403_and_never_reaches_the_backend()
    {
        var response = await NewGateway().WithKey(DevSeed.SuspendedCo.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        // A 200 here would be the echo upstream answering; the problem body proves the gateway stopped it.
        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "merchant_suspended");
    }

    [Fact]
    public async Task A_revoked_key_keeps_working_until_the_cache_ttl_then_gets_401()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync(ct: Ct);
        var client = gateway.WithKey(merchant.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);   // now cached

        var keyId = (await postgres.CreateDbContext().ApiKeys.SingleAsync(k => k.MerchantId == merchant.MerchantId, Ct)).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await gateway.Admin().DeleteAsync($"/admin/keys/{keyId}", Ct)).StatusCode);

        // The documented trade-off (spec §8): up to 60 s of staleness, in exchange for no DB on the hot path.
        gateway.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);

        gateway.Time.Advance(TimeSpan.FromSeconds(1));
        await Problems.AssertAsync(await client.GetAsync("/v1/payments/pay_1", Ct), HttpStatusCode.Unauthorized, "invalid_api_key");
    }

    [Fact]
    public async Task Suspending_a_merchant_takes_effect_after_the_cache_ttl()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync(ct: Ct);
        var client = gateway.WithKey(merchant.ApiKey);
        await client.GetAsync("/v1/payments/pay_1", Ct);

        await gateway.Admin().PatchAsJsonAsync($"/admin/merchants/{merchant.MerchantId}", new { status = "suspended" }, Ct);
        gateway.Time.Advance(TimeSpan.FromSeconds(60));

        await Problems.AssertAsync(await client.GetAsync("/v1/payments/pay_1", Ct), HttpStatusCode.Forbidden, "merchant_suspended");
    }

    [Fact]
    public async Task An_unknown_key_is_negatively_cached_for_ten_seconds()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync(ct: Ct);
        var key = ApiKey.Generate();
        var client = gateway.WithKey(key);
        await Problems.AssertAsync(await client.GetAsync("/v1/payments/pay_1", Ct), HttpStatusCode.Unauthorized, "invalid_api_key");

        // The key comes into existence after the gateway has cached "no such key".
        await using (var db = postgres.CreateDbContext())
        {
            db.ApiKeys.Add(new ApiKeyRecord
            {
                Id = Guid.NewGuid(), MerchantId = merchant.MerchantId,
                KeyPrefix = ApiKey.PrefixOf(key), KeyHash = ApiKey.Hash(key),
            });
            await db.SaveChangesAsync(Ct);
        }

        gateway.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);

        gateway.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/payments/pay_1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Keys_are_stored_only_as_a_sha256_hash_and_a_short_prefix()
    {
        var merchant = await NewGateway().CreateMerchantAsync(ct: Ct);

        var row = await postgres.CreateDbContext().ApiKeys.AsNoTracking().SingleAsync(k => k.MerchantId == merchant.MerchantId, Ct);
        Assert.Equal(ApiKey.Hash(merchant.ApiKey), row.KeyHash);
        Assert.Equal(merchant.ApiKey[..12], row.KeyPrefix);
        Assert.Equal(merchant.KeyPrefix, row.KeyPrefix);
    }

    [Fact]
    public async Task With_the_database_down_an_uncached_key_gets_503_auth_unavailable()
    {
        // ADR-0008. Port 1 refuses connections at once, so this is an outage without a slow timeout.
        var gateway = NewGateway(postgresOverride: "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x");

        var response = await gateway.WithKey(DevSeed.QuietShop.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        await Problems.AssertAsync(response, HttpStatusCode.ServiceUnavailable, "auth_unavailable");
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}
