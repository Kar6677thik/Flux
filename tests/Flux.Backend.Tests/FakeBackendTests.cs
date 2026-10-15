using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Flux.Backend.Tests;

public sealed class FakeBackendTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FakeBackendTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient ClientFor(string serviceName) => _factory
        .WithWebHostBuilder(b => b
            .UseSetting("Backend:ServiceName", serviceName)
            .UseSetting("Backend:LatencyMs", "0"))
        .CreateClient();

    private static HttpRequestMessage Post(string path, object body, string? merchantId = "m_test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (merchantId is not null)
            request.Headers.Add("X-Flux-Merchant-Id", merchantId);
        return request;
    }

    [Fact]
    public async Task Rejects_requests_that_did_not_come_through_the_gateway()
    {
        var response = await ClientFor("payments").SendAsync(Post("/payments", new { amount = 100 }, merchantId: null), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("missing_merchant_context", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Creates_a_payment_for_the_merchant_named_by_the_gateway()
    {
        var response = await ClientFor("payments").SendAsync(Post("/payments", new { amount = 50_000, currency = "INR" }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("pay_", payment.GetProperty("id").GetString());
        Assert.Equal("m_test", payment.GetProperty("merchantId").GetString());
        Assert.Equal("payments", payment.GetProperty("servedBy").GetString());
    }

    [Theory]
    [InlineData(0, "INR")]
    [InlineData(-5, "INR")]
    [InlineData(100, "inr")]
    [InlineData(100, "RUPEE")]
    public async Task Rejects_invalid_payments(long amount, string currency)
    {
        var response = await ClientFor("payments").SendAsync(Post("/payments", new { amount, currency }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Creates_a_refund_against_a_payment_id()
    {
        var response = await ClientFor("refunds").SendAsync(Post("/refunds", new { paymentId = "pay_abc", amount = 1_000 }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var refund = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("rfnd_", refund.GetProperty("id").GetString());
        Assert.Equal("refunds", refund.GetProperty("servedBy").GetString());
    }

    [Fact]
    public async Task Each_service_only_exposes_its_own_endpoints()
    {
        var refunds = ClientFor("refunds");

        var response = await refunds.SendAsync(Post("/payments", new { amount = 100 }), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_check_needs_no_merchant_context()
    {
        var response = await ClientFor("payments").GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Refuses_to_start_without_a_valid_service_name()
    {
        var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Backend:ServiceName", "invoices"));

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
