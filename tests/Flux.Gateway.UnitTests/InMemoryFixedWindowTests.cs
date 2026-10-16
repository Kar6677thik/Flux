using Flux.Gateway.RateLimiting;
using Flux.Gateway.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Flux.Gateway.UnitTests;

/// <summary>
/// The fixed window (spec §9.3), including its flaw, at exact instants. Windows are aligned to the
/// epoch, so 09:00:10.000 UTC is a boundary when W = 10 s.
/// </summary>
public sealed class InMemoryFixedWindowTests
{
    private static readonly RouteLimit Free = new(RefillPerSecond: 10, BurstCapacity: 10);
    private static readonly RateLimitKey Key = new(Guid.NewGuid(), RouteGroups.Payments);
    private static readonly DateTimeOffset Boundary = new(2026, 10, 15, 9, 0, 10, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Boundary.AddMilliseconds(-100));   // 9.9 s into a window
    private readonly InMemoryFixedWindow _limiter;

    public InMemoryFixedWindowTests() =>
        _limiter = new InMemoryFixedWindow(_time, Options.Create(new RateLimitingOptions { FixedWindow = { WindowSeconds = 10 } }));

    private RateLimitDecision Take(RateLimitKey? key = null) => _limiter.TryAcquireAsync(key ?? Key, Free, CancellationToken.None).Result;

    [Fact]
    public void The_quota_is_rate_times_window()
    {
        var decisions = Enumerable.Range(0, 101).Select(_ => Take()).ToList();

        Assert.Equal(100, decisions.Count(d => d.Allowed));
        Assert.Equal(100, decisions[0].Limit);
        Assert.Equal(99, decisions[0].Remaining);
        Assert.False(decisions[100].Allowed);
    }

    [Fact]
    public void Retry_after_and_reset_point_at_the_next_boundary()
    {
        for (var i = 0; i < 100; i++) Take();

        var limited = Take();

        Assert.Equal(TimeSpan.FromMilliseconds(100), limited.RetryAfter);
        Assert.Equal(TimeSpan.FromMilliseconds(100), limited.ResetAfter);
    }

    [Fact]
    public void The_boundary_burst_allows_twice_the_quota_in_200_ms()
    {
        // This is the flaw E2 measures for real: 100 just before the boundary…
        var before = Enumerable.Range(0, 100).Count(_ => Take().Allowed);
        Assert.False(Take().Allowed);

        // … and 100 more just after it, because the counter simply starts again.
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var after = Enumerable.Range(0, 100).Count(_ => Take().Allowed);

        Assert.Equal(200, before + after);   // the plan says 10/s; this is 200 in 0.2 s
    }

    [Fact]
    public void A_token_bucket_on_the_same_plan_does_not_have_the_flaw()
    {
        var bucket = new InMemoryTokenBucket(_time);
        RateLimitDecision TakeFromBucket() => bucket.TryAcquireAsync(Key, Free, CancellationToken.None).Result;

        var before = Enumerable.Range(0, 100).Count(_ => TakeFromBucket().Allowed);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var after = Enumerable.Range(0, 100).Count(_ => TakeFromBucket().Allowed);

        Assert.Equal(10 + 2, before + after);   // the burst of B, plus 0.2 s × 10/s of refill
    }

    [Fact]
    public void Keys_have_separate_windows()
    {
        for (var i = 0; i < 100; i++) Take();

        var other = Take(Key with { RouteGroup = RouteGroups.Refunds });

        Assert.True(other.Allowed);
    }

    [Fact]
    public void Old_windows_are_swept()
    {
        Take();
        _time.Advance(TimeSpan.FromMinutes(2));

        Take(Key with { RouteGroup = RouteGroups.Refunds });

        Assert.Equal(1, _limiter.WindowCount);
    }
}
