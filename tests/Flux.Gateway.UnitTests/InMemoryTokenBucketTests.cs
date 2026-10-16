using Flux.Gateway.RateLimiting;
using Flux.Gateway.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Flux.Gateway.UnitTests;

/// <summary>
/// The token-bucket maths from spec §9.2, checked at exact instants. Time only moves when a test
/// calls Advance(), so "0.35 s later" means exactly 0.35 s, every run.
/// </summary>
public sealed class InMemoryTokenBucketTests
{
    private static readonly RouteLimit Free = new(RefillPerSecond: 10, BurstCapacity: 10);
    private static readonly RateLimitKey Key = new(Guid.NewGuid(), RouteGroups.Payments);

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryTokenBucket _limiter;

    public InMemoryTokenBucketTests() => _limiter = new InMemoryTokenBucket(_time);

    private RateLimitDecision Take(RouteLimit? limit = null, RateLimitKey? key = null) =>
        _limiter.TryAcquireAsync(key ?? Key, limit ?? Free, CancellationToken.None).Result;

    [Fact]
    public void A_new_bucket_starts_full_and_allows_exactly_its_burst()
    {
        var decisions = Enumerable.Range(0, 11).Select(_ => Take()).ToList();

        Assert.All(decisions.Take(10), d => Assert.True(d.Allowed));
        Assert.Equal([9, 8, 7, 6, 5, 4, 3, 2, 1, 0], decisions.Take(10).Select(d => d.Remaining));
        Assert.False(decisions[10].Allowed);
    }

    [Fact]
    public void Journal_00_exercise_1_twenty_five_at_once_then_one_at_350ms()
    {
        var burst = Enumerable.Range(0, 25).Count(_ => Take().Allowed);
        _time.Advance(TimeSpan.FromMilliseconds(350));
        var late = Take();

        Assert.Equal(10, burst);
        Assert.True(late.Allowed);       // 0.35 s × 10/s = 3.5 tokens accrued
        Assert.Equal(2, late.Remaining); // 3.5 − 1 = 2.5, floored
    }

    [Fact]
    public void Retry_after_is_the_exact_time_until_one_whole_token()
    {
        for (var i = 0; i < 10; i++) Take();
        _time.Advance(TimeSpan.FromMilliseconds(30));   // 0.3 tokens

        var limited = Take();

        Assert.False(limited.Allowed);
        Assert.Equal(TimeSpan.FromMilliseconds(70), limited.RetryAfter);   // (1 − 0.3) / 10
        Assert.Equal(TimeSpan.FromMilliseconds(970), limited.ResetAfter);  // (10 − 0.3) / 10
    }

    [Fact]
    public void Waiting_exactly_retry_after_is_enough()
    {
        for (var i = 0; i < 10; i++) Take();
        var limited = Take();

        _time.Advance(limited.RetryAfter);

        Assert.True(Take().Allowed);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(10)]
    [InlineData(70)]
    public void Waiting_exactly_retry_after_is_enough_from_a_fractional_bucket(int partialMs)
    {
        for (var i = 0; i < 10; i++) Take();
        _time.Advance(TimeSpan.FromMilliseconds(partialMs));
        var limited = Take();
        Assert.False(limited.Allowed);

        _time.Advance(limited.RetryAfter);

        Assert.True(Take().Allowed);
    }

    [Fact]
    public void Refill_never_exceeds_capacity()
    {
        Take();
        _time.Advance(TimeSpan.FromHours(1));   // would be 36,000 tokens without the cap

        var burst = Enumerable.Range(0, 20).Count(_ => Take().Allowed);

        Assert.Equal(10, burst);
    }

    [Fact]
    public void The_average_rate_is_enforced_over_a_long_run()
    {
        // One request every 50 ms for 10 s = 20/s offered against 10/s allowed.
        var allowed = 0;
        for (var i = 0; i < 200; i++)
        {
            if (Take().Allowed) allowed++;
            _time.Advance(TimeSpan.FromMilliseconds(50));
        }

        // Requests at t = 0, 0.05 … 9.95 s: B + r × 9.95 s = 10 + 99.5 tokens, so 109 whole requests.
        Assert.Equal(109, allowed);
    }

    [Fact]
    public void A_fractional_refill_rate_works()
    {
        var slow = new RouteLimit(RefillPerSecond: 0.5, BurstCapacity: 1);
        Assert.True(Take(slow).Allowed);

        var limited = Take(slow);

        Assert.False(limited.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(2), limited.RetryAfter);
    }

    [Fact]
    public void Merchants_and_route_groups_have_separate_buckets()
    {
        var noisy = new RateLimitKey(Guid.NewGuid(), RouteGroups.Payments);
        for (var i = 0; i < 10; i++) Take(key: noisy);
        Assert.False(Take(key: noisy).Allowed);

        Assert.True(Take(key: noisy with { RouteGroup = RouteGroups.Refunds }).Allowed);
        Assert.True(Take(key: new RateLimitKey(Guid.NewGuid(), RouteGroups.Payments)).Allowed);
    }

    [Fact]
    public async Task Concurrent_requests_never_spend_the_same_token_twice()
    {
        var limit = new RouteLimit(RefillPerSecond: 1, BurstCapacity: 100);

        // 1,000 requests race for 100 tokens with the clock frozen: a missing lock shows up here
        // as more than 100 allowed (two threads both reading "1 left" and both spending it).
        var decisions = await Task.WhenAll(Enumerable.Range(0, 1_000)
            .Select(_ => Task.Run(() => _limiter.TryAcquireAsync(Key, limit, CancellationToken.None).AsTask())));

        Assert.Equal(100, decisions.Count(d => d.Allowed));
    }

    [Fact]
    public void Buckets_idle_for_ten_minutes_are_evicted()
    {
        Take(key: new RateLimitKey(Guid.NewGuid(), RouteGroups.Payments));
        Take(key: new RateLimitKey(Guid.NewGuid(), RouteGroups.Refunds));
        Assert.Equal(2, _limiter.BucketCount);

        _time.Advance(InMemoryTokenBucket.IdleEviction);
        Take();   // any request triggers the sweep

        Assert.Equal(1, _limiter.BucketCount);   // only the bucket this request just used
    }
}
