using System.Net;
using System.Net.Http.Json;
using Flux.Gateway.Data;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// M1 behaviour: routing and path rewriting, correlation ids, header stripping, error contract.
/// Since M2 every /v1 request needs a key, so these send the seeded quiet-shop key.
/// </summary>
public sealed class PassThroughProxyTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    // Port 1 is reserved and never listening, so connecting to it fails immediately.
    private const string DeadUpstream = "http://127.0.0.1:1";

    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Gateway(string? paymentsAddress = null, string? refundsAddress = null, string activityTimeout = "00:00:10")
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address, new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:payments:Destinations:d1:Address"] = paymentsAddress ?? upstream.Address,
            ["ReverseProxy:Clusters:payments:HttpRequest:ActivityTimeout"] = activityTimeout,
            ["ReverseProxy:Clusters:refunds:Destinations:d1:Address"] = refundsAddress ?? upstream.Address,
        });
        _gateways.Add(gateway);
        return gateway.WithKey(DevSeed.QuietShop.ApiKey);
    }

    [Theory]
    [InlineData("POST", "/v1/payments", "/payments")]
    [InlineData("GET", "/v1/payments/pay_123", "/payments/pay_123")]
    [InlineData("POST", "/v1/refunds", "/refunds")]
    [InlineData("GET", "/v1/refunds/rfnd_9", "/refunds/rfnd_9")]
    public async Task Forwards_public_routes_to_upstream_paths(string method, string publicPath, string upstreamPath)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), publicPath);
        if (method == "POST")
            request.Content = JsonContent.Create(new { amount = 100 });

        var response = await Gateway().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<EchoedRequest>(Ct);
        Assert.Equal(method, echo!.Method);
        Assert.Equal(upstreamPath, echo.Path);
    }

    [Fact]
    public async Task Client_supplied_flux_headers_are_replaced_by_the_gateways_own()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_1");
        request.Headers.Add("X-Flux-Merchant-Id", "someone-else");
        request.Headers.Add("x-flux-plan", "enterprise");
        request.Headers.Add("X-Flux-Anything", "x");

        var response = await Gateway().SendAsync(request, Ct);

        // The headers that remain are the ones auth set from the key, with no trace of the forgeries.
        var echo = await response.Content.ReadFromJsonAsync<EchoedRequest>(Ct);
        Assert.Equal(DevSeed.QuietShop.Id.ToString(), echo!.Header("X-Flux-Merchant-Id"));
        Assert.Equal("pro", echo.Header("X-Flux-Plan"));
        Assert.Null(echo.Header("X-Flux-Anything"));
    }

    [Fact]
    public async Task Generates_a_request_id_and_sends_it_both_upstream_and_back()
    {
        var response = await Gateway().GetAsync("/v1/payments/pay_1", Ct);

        var returned = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        var echo = await response.Content.ReadFromJsonAsync<EchoedRequest>(Ct);
        Assert.Matches("^[0-9a-f]{32}$", returned);
        Assert.Equal(returned, echo!.Header("X-Request-Id"));
    }

    [Theory]
    [InlineData("client-trace_42", true)]
    [InlineData("has spaces", false)]
    [InlineData("lineébreak", false)]
    [InlineData("an-id-that-is-far-too-long-to-be-reasonable-0123456789012345678901234567890", false)]
    public async Task Keeps_a_client_request_id_only_if_it_is_safe(string clientId, bool kept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_1");
        request.Headers.TryAddWithoutValidation("X-Request-Id", clientId);

        var response = await Gateway().SendAsync(request, Ct);

        var returned = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.Equal(kept, returned == clientId);
    }

    [Theory]
    [InlineData("GET", "/v1/nope")]
    [InlineData("DELETE", "/v1/payments/pay_1")]
    public async Task Unknown_routes_get_a_problem_details_404(string method, string path)
    {
        var response = await Gateway().SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "route_not_found");
    }

    [Fact]
    public async Task Unreachable_upstream_becomes_502_upstream_unavailable()
    {
        var response = await Gateway(refundsAddress: DeadUpstream).GetAsync("/v1/refunds/rfnd_1", Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadGateway, "upstream_unavailable");
    }

    [Fact]
    public async Task Slow_upstream_becomes_504_upstream_timeout()
    {
        var response = await Gateway(activityTimeout: "00:00:00.300").GetAsync("/v1/payments/slow", Ct);

        await Problems.AssertAsync(response, HttpStatusCode.GatewayTimeout, "upstream_timeout");
    }

    [Fact]
    public async Task Liveness_answers_without_an_upstream()
    {
        var response = await Gateway(DeadUpstream, DeadUpstream).GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}
