using Flux.Gateway.Auth;
using Flux.Gateway.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Flux.Gateway.UnitTests;

/// <summary>The TTLs are tested at exact instants: time only moves when the test says so.</summary>
public sealed class ApiKeyCacheTests
{
    private static readonly AuthenticatedMerchant Merchant = new(Guid.NewGuid(), "free", MerchantStatus.Active, "flux_abcdefg");

    private readonly FakeTimeProvider _time = new();

    private ApiKeyCache Cache(int maxEntries = 100) => new(_time, Options.Create(new ApiKeyOptions
    {
        CacheTtlSeconds = 60,
        NegativeCacheTtlSeconds = 10,
        MaxCacheEntries = maxEntries,
    }));

    [Fact]
    public void A_found_key_is_served_from_cache_until_its_ttl_ends()
    {
        var cache = Cache();
        cache.SetFound("h1", Merchant);

        _time.Advance(TimeSpan.FromSeconds(59.999));
        Assert.True(cache.TryGet("h1", out var hit));
        Assert.Same(Merchant, hit);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(cache.TryGet("h1", out _));
    }

    [Fact]
    public void An_unknown_key_is_remembered_for_the_shorter_negative_ttl()
    {
        var cache = Cache();
        cache.SetNotFound("h1");

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(cache.TryGet("h1", out var hit));
        Assert.Null(hit);   // a hit that means "known not to exist"

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet("h1", out _));
    }

    [Fact]
    public void Finding_a_key_replaces_a_cached_miss()
    {
        var cache = Cache();
        cache.SetNotFound("h1");

        cache.SetFound("h1", Merchant);

        Assert.True(cache.TryGet("h1", out var hit));
        Assert.Same(Merchant, hit);
    }

    [Fact]
    public void The_negative_cache_stops_growing_at_its_cap()
    {
        var cache = Cache(maxEntries: 2);
        cache.SetNotFound("h1");
        cache.SetNotFound("h2");

        cache.SetNotFound("h3");

        Assert.False(cache.TryGet("h3", out _));
        Assert.True(cache.TryGet("h1", out _));
    }

    [Fact]
    public void A_full_negative_cache_makes_room_by_dropping_expired_entries()
    {
        var cache = Cache(maxEntries: 2);
        cache.SetNotFound("h1");
        cache.SetNotFound("h2");
        _time.Advance(TimeSpan.FromSeconds(10));

        cache.SetNotFound("h3");

        Assert.True(cache.TryGet("h3", out _));
    }

    [Fact]
    public void The_cap_does_not_limit_found_keys()
    {
        var cache = Cache(maxEntries: 1);
        cache.SetNotFound("bogus");

        cache.SetFound("h1", Merchant);
        cache.SetFound("h2", Merchant);

        Assert.True(cache.TryGet("h1", out _));
        Assert.True(cache.TryGet("h2", out _));
    }
}
