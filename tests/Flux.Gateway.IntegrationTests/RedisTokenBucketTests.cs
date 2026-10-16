using System.Globalization;
using Flux.Gateway.RateLimiting;
using Flux.Gateway.Tenancy;
using StackExchange.Redis;

namespace Flux.Gateway.IntegrationTests;

/// <summary>
/// token_bucket.lua against a real Redis (spec §9.4, §14). Redis's clock can't be faked, so tests
/// that need time to pass move the bucket's stored timestamp back instead of sleeping.
/// </summary>
public sealed class RedisTokenBucketTests(RedisFixture redis) : IAsyncDisposable
{
    private static readonly RouteLimit Free = new(RefillPerSecond: 10, BurstCapacity: 10);
    // Real Redis time passes during a test; at 0.01/s it can't add a whole token.
    private static readonly RouteLimit Slow = new(RefillPerSecond: 0.01, BurstCapacity: 10);
    private static readonly RouteLimit OnePerSecond = new(RefillPerSecond: 1, BurstCapacity: 10);

    private readonly RedisConnection _connection = RedisFixture.Connect(redis.ConnectionString);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RateLimitKey NewKey() => new(Guid.NewGuid(), RouteGroups.Payments);

    private async Task<IDatabase> Database() => (await _connection.GetAsync()).GetDatabase();

    [Fact]
    public async Task A_new_bucket_allows_its_burst_then_refuses()
    {
        var limiter = new RedisTokenBucket(_connection);
        var key = NewKey();

        var decisions = new List<RateLimitDecision>();
        for (var i = 0; i < 11; i++) decisions.Add(await limiter.TryAcquireAsync(key, Slow, Ct));

        Assert.Equal([9, 8, 7, 6, 5, 4, 3, 2, 1, 0], decisions.Take(10).Select(d => d.Remaining));
        Assert.All(decisions.Take(10), d => Assert.True(d.Allowed));
        var refused = decisions[10];
        Assert.False(refused.Allowed);
        // (1 − tokens) / r ≈ 100 s, a hair under: real microseconds pass between calls.
        Assert.InRange(refused.RetryAfter, TimeSpan.FromSeconds(99), TimeSpan.FromSeconds(100));
    }

    [Fact]
    public async Task The_bucket_is_a_hash_with_tokens_and_a_microsecond_timestamp_and_expires()
    {
        var key = NewKey();
        await new RedisTokenBucket(_connection).TryAcquireAsync(key, Free, Ct);
        var db = await Database();

        var tokens = double.Parse((string)(await db.HashGetAsync(RedisTokenBucket.KeyFor(key), "tokens"))!, CultureInfo.InvariantCulture);
        var ttl = await db.KeyTimeToLiveAsync(RedisTokenBucket.KeyFor(key));

        Assert.Equal(9, tokens, precision: 3);
        Assert.Equal($"flux:rl:{{{key.MerchantId}}}:payments", RedisTokenBucket.KeyFor(key).ToString());
        // ceil(B / r × 1000) + 1000 = 2000 ms: by then an untouched bucket would be full anyway.
        Assert.InRange(ttl!.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2000));
    }

    [Fact]
    public async Task Tokens_refill_by_elapsed_redis_time()
    {
        var limiter = new RedisTokenBucket(_connection);
        var key = NewKey();
        for (var i = 0; i < 10; i++) await limiter.TryAcquireAsync(key, OnePerSecond, Ct);
        var db = await Database();

        // Pretend the last refill was 5 s earlier, without waiting 5 s: 5 tokens accrue at 1/s.
        var ts = (long)await db.HashGetAsync(RedisTokenBucket.KeyFor(key), "ts");
        await db.HashSetAsync(RedisTokenBucket.KeyFor(key), "ts", ts - 5_000_000);
        var decision = await limiter.TryAcquireAsync(key, OnePerSecond, Ct);

        Assert.True(decision.Allowed);
        Assert.Equal(4, decision.Remaining);   // 0 + 5 − 1
    }

    [Fact]
    public async Task Five_hundred_parallel_requests_from_two_gateways_allow_exactly_the_burst()
    {
        // Two connections = two gateway instances. With in-memory buckets each would allow 100
        // (experiment E1). Sharing one bucket through one atomic script, they allow 100 together.
        await using var second = RedisFixture.Connect(redis.ConnectionString);
        var gatewayA = new RedisTokenBucket(_connection);
        var gatewayB = new RedisTokenBucket(second);
        var limit = new RouteLimit(RefillPerSecond: 0.001, BurstCapacity: 100);   // ≈ no refill during the test
        var key = NewKey();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 500).Select(i =>
            (i % 2 == 0 ? gatewayA : gatewayB).TryAcquireAsync(key, limit, Ct).AsTask()));

        Assert.Equal(100, decisions.Count(d => d.Allowed));
    }

    [Fact]
    public async Task The_script_survives_redis_forgetting_it()
    {
        var limiter = new RedisTokenBucket(_connection);
        var key = NewKey();
        await limiter.TryAcquireAsync(key, Slow, Ct);

        // What a Redis restart does to the script cache. The client must notice NOSCRIPT and resend.
        await using (var admin = await ConnectionMultiplexer.ConnectAsync($"{redis.ConnectionString},allowAdmin=true"))
            await admin.GetServers()[0].ScriptFlushAsync();

        Assert.Equal(8, (await limiter.TryAcquireAsync(key, Slow, Ct)).Remaining);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
