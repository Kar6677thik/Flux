using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flux.Gateway.Admin;
using Flux.Gateway.Auth;

namespace Flux.Gateway.IntegrationTests;

/// <summary>Spec §11.1: the admin API, its token check, and what it reveals (prefixes, never keys).</summary>
public sealed class AdminApiTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayFactory NewGateway(Dictionary<string, string?>? settings = null, string environment = "Development")
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address, settings, environment);
        _gateways.Add(gateway);
        return gateway;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData(GatewayFactory.AdminToken + "x")]   // right prefix, wrong length
    public async Task Admin_endpoints_require_the_admin_token(string? token)
    {
        var client = NewGateway().CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Add(AdminTokenFilter.HeaderName, token);

        var response = await client.PostAsJsonAsync("/admin/merchants", new { name = "x", plan = "free" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid_admin_token");
    }

    [Fact]
    public async Task Without_a_configured_token_the_admin_api_does_not_exist()
    {
        var client = NewGateway(new() { ["Admin:Token"] = "" }).CreateClient();
        client.DefaultRequestHeaders.Add(AdminTokenFilter.HeaderName, "");

        var response = await client.PostAsJsonAsync("/admin/merchants", new { name = "x", plan = "free" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "route_not_found");
    }

    [Fact]
    public async Task Creating_a_merchant_returns_a_working_key_exactly_once()
    {
        var gateway = NewGateway();

        var created = await gateway.CreateMerchantAsync("pro", Ct);

        Assert.True(ApiKey.IsWellFormed(created.ApiKey));
        Assert.Equal(created.ApiKey[..12], created.KeyPrefix);
        Assert.Equal(HttpStatusCode.OK, (await gateway.WithKey(created.ApiKey).GetAsync("/v1/payments/pay_1", Ct)).StatusCode);

        var view = await gateway.Admin().GetStringAsync($"/admin/merchants/{created.MerchantId}", Ct);
        Assert.DoesNotContain(created.ApiKey, view);
        var merchant = JsonSerializer.Deserialize<MerchantView>(view, JsonSerializerOptions.Web)!;
        Assert.Equal("pro", merchant.Plan);
        Assert.Equal("active", merchant.Status);
        Assert.Equal(created.KeyPrefix, Assert.Single(merchant.Keys).KeyPrefix);
    }

    [Theory]
    [InlineData("""{ "name": "shop", "plan": "platinum" }""")]
    [InlineData("""{ "name": "shop" }""")]
    [InlineData("""{ "name": "   ", "plan": "free" }""")]
    [InlineData("""{ "plan": "free" }""")]
    public async Task Invalid_merchant_requests_are_400_invalid_request(string json)
    {
        var response = await NewGateway().Admin().PostAsync("/admin/merchants", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]   // minimal APIs handle bad bodies differently per environment by default
    public async Task Malformed_json_is_400_invalid_request(string environment)
    {
        var response = await NewGateway(environment: environment).Admin().PostAsync("/admin/merchants", new StringContent("{ not json", Encoding.UTF8, "application/json"), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Patch_changes_plan_and_status_and_validates_them()
    {
        var gateway = NewGateway();
        var admin = gateway.Admin();
        var created = await gateway.CreateMerchantAsync("free", Ct);
        var url = $"/admin/merchants/{created.MerchantId}";

        var updated = await admin.PatchAsJsonAsync(url, new { plan = "pro", status = "suspended" }, Ct);
        var view = await updated.Content.ReadFromJsonAsync<MerchantView>(Ct);
        Assert.Equal(("pro", "suspended"), (view!.Plan, view.Status));

        await Problems.AssertAsync(await admin.PatchAsJsonAsync(url, new { status = "deleted" }, Ct), HttpStatusCode.BadRequest, "invalid_request");
        await Problems.AssertAsync(await admin.PatchAsJsonAsync(url, new { status = "1" }, Ct), HttpStatusCode.BadRequest, "invalid_request");
        await Problems.AssertAsync(await admin.PatchAsJsonAsync(url, new { plan = "platinum" }, Ct), HttpStatusCode.BadRequest, "invalid_request");
        await Problems.AssertAsync(await admin.PatchAsJsonAsync(url, new { }, Ct), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Unknown_merchants_and_keys_are_404()
    {
        var admin = NewGateway().Admin();
        var id = Guid.NewGuid();

        await Problems.AssertAsync(await admin.GetAsync($"/admin/merchants/{id}", Ct), HttpStatusCode.NotFound, "merchant_not_found");
        await Problems.AssertAsync(await admin.PatchAsJsonAsync($"/admin/merchants/{id}", new { status = "active" }, Ct), HttpStatusCode.NotFound, "merchant_not_found");
        await Problems.AssertAsync(await admin.PostAsync($"/admin/merchants/{id}/keys", null, Ct), HttpStatusCode.NotFound, "merchant_not_found");
        await Problems.AssertAsync(await admin.DeleteAsync($"/admin/keys/{id}", Ct), HttpStatusCode.NotFound, "key_not_found");
    }

    [Fact]
    public async Task A_second_key_allows_rotation_without_downtime()
    {
        var gateway = NewGateway();
        var admin = gateway.Admin();
        var created = await gateway.CreateMerchantAsync(ct: Ct);

        var second = await (await admin.PostAsync($"/admin/merchants/{created.MerchantId}/keys", null, Ct))
            .Content.ReadFromJsonAsync<CreatedKeyResponse>(Ct);
        var firstKeyId = (await admin.GetFromJsonAsync<MerchantView>($"/admin/merchants/{created.MerchantId}", Ct))!.Keys[0].KeyId;

        // Both keys work at once; the client switches over, then the old key is revoked.
        Assert.Equal(HttpStatusCode.OK, (await gateway.WithKey(created.ApiKey).GetAsync("/v1/refunds/rfnd_1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gateway.WithKey(second!.ApiKey).GetAsync("/v1/refunds/rfnd_1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/admin/keys/{firstKeyId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/admin/keys/{firstKeyId}", Ct)).StatusCode);   // idempotent

        var keys = (await admin.GetFromJsonAsync<MerchantView>($"/admin/merchants/{created.MerchantId}", Ct))!.Keys;
        Assert.NotNull(keys.Single(k => k.KeyId == firstKeyId).RevokedAt);
        Assert.Null(keys.Single(k => k.KeyId == second.KeyId).RevokedAt);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}
