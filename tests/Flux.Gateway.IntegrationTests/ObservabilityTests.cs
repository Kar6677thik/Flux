using System.Net;
using Flux.Gateway.Auth;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// Spec §11.2: what /metrics exposes (and doesn't), and the Server-Timing header.
/// These run alone: an OpenTelemetry MeterListener is process-wide and subscribes to every meter
/// named "Flux.Gateway", so gateways that other tests run in parallel would add to our counts. In
/// production there is one gateway per process, so this only matters inside a test run.
/// </summary>
[Collection(nameof(MetricsCollection))]
public sealed class ObservabilityTests(EchoUpstream upstream, PostgresFixture postgres) : IClassFixture<EchoUpstream>, IAsyncDisposable
{
    private readonly List<GatewayFactory> _gateways = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayFactory NewGateway()
    {
        var gateway = new GatewayFactory(postgres.ConnectionString, upstream.Address);
        _gateways.Add(gateway);
        return gateway;
    }

    [Fact]
    public async Task Metrics_count_each_outcome_by_route_group()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync("free", Ct);
        var client = gateway.WithKey(merchant.ApiKey);
        for (var i = 0; i < 3; i++) await client.PostAsync("/v1/refunds", null, Ct);   // free refunds: 2 allowed, 1 limited
        await gateway.CreateClient().GetAsync("/v1/payments/pay_1", Ct);                // no key
        await gateway.WithKey(ApiKey.Generate()).GetAsync("/v1/payments/pay_1", Ct);    // unknown key
        await gateway.CreateClient().GetAsync("/v1/nope", Ct);

        var metrics = await gateway.CreateClient().GetStringAsync("/metrics", Ct);

        Assert.Contains("""flux_requests_total{otel_scope_name="Flux.Gateway",outcome="allowed",route_group="refunds"} 2""", metrics);
        Assert.Contains("""flux_requests_total{otel_scope_name="Flux.Gateway",outcome="limited",route_group="refunds"} 1""", metrics);
        Assert.Contains("""flux_requests_total{otel_scope_name="Flux.Gateway",outcome="unauthorized",route_group="payments"} 2""", metrics);
        Assert.Contains("""flux_requests_total{otel_scope_name="Flux.Gateway",outcome="not_found",route_group="none"} 1""", metrics);
    }

    [Fact]
    public async Task Latency_histograms_are_exported_in_seconds_with_sub_millisecond_buckets()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync("pro", Ct);
        await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        var metrics = await gateway.CreateClient().GetStringAsync("/metrics", Ct);

        Assert.Contains("flux_gateway_overhead_seconds_bucket", metrics);
        Assert.Contains("le=\"0.00025\"", metrics);
        Assert.Contains("""flux_ratelimiter_duration_seconds_count{otel_scope_name="Flux.Gateway",algorithm="InMemoryTokenBucket"} 1""", metrics);
    }

    [Fact]
    public async Task No_metric_carries_a_merchant_id()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync("free", Ct);
        await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        var metrics = await gateway.CreateClient().GetStringAsync("/metrics", Ct);

        // Cardinality: one label value per merchant would mean one time series per merchant.
        Assert.DoesNotContain(merchant.MerchantId.ToString(), metrics);
        Assert.DoesNotContain("merchant", metrics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_responses_split_server_timing_into_gateway_and_upstream()
    {
        var gateway = NewGateway();
        var merchant = await gateway.CreateMerchantAsync("pro", Ct);

        var response = await gateway.WithKey(merchant.ApiKey).GetAsync("/v1/payments/pay_1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"^gw;dur=\d+(\.\d+)?, upstream;dur=\d+(\.\d+)?$", Assert.Single(response.Headers.GetValues("Server-Timing")));
    }

    [Fact]
    public async Task Rejected_responses_have_only_gateway_time()
    {
        var response = await NewGateway().CreateClient().GetAsync("/v1/payments/pay_1", Ct);

        Assert.Matches(@"^gw;dur=\d+(\.\d+)?$", Assert.Single(response.Headers.GetValues("Server-Timing")));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gateway in _gateways)
            await gateway.DisposeAsync();
    }
}

[CollectionDefinition(nameof(MetricsCollection), DisableParallelization = true)]
public sealed class MetricsCollection;
