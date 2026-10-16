using Flux.Gateway.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Flux.Gateway.UnitTests;

public sealed class RateLimitHeadersTests
{
    private static IHeaderDictionary Apply(RateLimitDecision decision)
    {
        IHeaderDictionary headers = new HeaderDictionary();
        RateLimitHeaders.Apply(headers, decision);
        return headers;
    }

    [Fact]
    public void An_allowed_request_gets_limit_remaining_and_reset_but_no_retry_after()
    {
        var headers = Apply(new RateLimitDecision(true, 10, 9, TimeSpan.Zero, TimeSpan.FromMilliseconds(100)));

        Assert.Equal("10", headers["X-RateLimit-Limit"]);
        Assert.Equal("9", headers["X-RateLimit-Remaining"]);
        Assert.Equal("1", headers["X-RateLimit-Reset"]);   // 0.1 s rounds *up*
        Assert.False(headers.ContainsKey("Retry-After"));
        Assert.False(headers.ContainsKey("X-RateLimit-Degraded"));
    }

    [Theory]
    [InlineData(70, "1")]      // 0.07 s → 1: never tell a client to come back before it's useful
    [InlineData(1000, "1")]    // exactly 1 s stays 1
    [InlineData(1001, "2")]
    [InlineData(0, "1")]       // a limited response always says "at least 1"
    public void Retry_after_rounds_up_to_whole_seconds_minimum_one(int retryAfterMs, string expected)
    {
        var headers = Apply(new RateLimitDecision(false, 10, 0, TimeSpan.FromMilliseconds(retryAfterMs), TimeSpan.FromSeconds(1)));

        Assert.Equal(expected, headers.RetryAfter);
    }

    [Fact]
    public void Floating_point_noise_does_not_add_a_second()
    {
        Assert.Equal(1, RateLimitHeaders.CeilingSeconds(TimeSpan.FromSeconds(1.0000000001)));
    }

    [Fact]
    public void A_fallback_decision_is_marked_degraded()
    {
        var headers = Apply(new RateLimitDecision(true, 10, 3, TimeSpan.Zero, TimeSpan.FromSeconds(1), DecisionSource.Fallback));

        Assert.Equal("true", headers["X-RateLimit-Degraded"]);
    }
}
