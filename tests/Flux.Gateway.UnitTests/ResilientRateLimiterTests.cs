using Flux.Gateway.RateLimiting;
using Polly.Timeout;
using StackExchange.Redis;

namespace Flux.Gateway.UnitTests;

/// <summary>
/// Which exceptions count as "Redis failed" (fall back) versus "something else" (propagate). The
/// behaviour against a real Redis, healthy, dead and stopped, is in the integration tests.
/// </summary>
public sealed class ResilientRateLimiterTests
{
    [Fact]
    public void Redis_errors_and_timeouts_are_failures()
    {
        Assert.True(ResilientRateLimiter.IsRedisFailure(new RedisException("connection refused"), CancellationToken.None));
        Assert.True(ResilientRateLimiter.IsRedisFailure(new TimeoutException("RedisTimeoutException derives from this"), CancellationToken.None));
        Assert.True(ResilientRateLimiter.IsRedisFailure(new TimeoutRejectedException(), CancellationToken.None));
    }

    [Fact]
    public void A_cancellation_nobody_asked_for_is_a_redis_failure()
    {
        // What StackExchange.Redis throws when it abandons a frozen connection (found in P4).
        Assert.True(ResilientRateLimiter.IsRedisFailure(new TaskCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void A_cancellation_the_caller_asked_for_is_not()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();   // the HTTP client hung up

        Assert.False(ResilientRateLimiter.IsRedisFailure(new TaskCanceledException(), aborted.Token));
    }

    [Fact]
    public void Bugs_are_not_hidden_as_outages()
    {
        Assert.False(ResilientRateLimiter.IsRedisFailure(new InvalidOperationException("a bug"), CancellationToken.None));
    }
}
