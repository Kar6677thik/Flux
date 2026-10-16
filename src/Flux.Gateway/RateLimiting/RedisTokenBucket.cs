using System.Globalization;
using Flux.Gateway.Tenancy;
using StackExchange.Redis;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// The production limiter (spec §9.4, ADR-0004): one bucket per merchant and route group, shared
/// by every gateway instance, updated by one atomic Lua script. This is the fix for experiment E1.
/// Failures propagate as exceptions; deciding what a failure *means* is ResilientRateLimiter's job.
/// </summary>
public sealed class RedisTokenBucket(RedisConnection connection) : IRateLimiter
{
    public const string Name = "RedisTokenBucket";

    // The client hashes the script once, sends EVALSHA from then on, and falls back to EVAL by
    // itself if Redis replies NOSCRIPT (after a restart, say), so only the 40-byte hash travels
    // on each call.
    internal static readonly string Script = LoadScript();

    public string Algorithm => Name;

    // The {…} hash tag means that in Redis Cluster only the merchant id picks the node, so all
    // of one merchant's buckets live together. With one node it only prepares for the future.
    public static RedisKey KeyFor(RateLimitKey key) => $"flux:rl:{{{key.MerchantId}}}:{key.RouteGroup}";

    public async ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct)
    {
        var redis = await connection.GetAsync();
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script, [KeyFor(key)],
        [
            limit.BurstCapacity,
            limit.RefillPerSecond.ToString("R", CultureInfo.InvariantCulture),
        ]))!;

        return new RateLimitDecision(
            Allowed: (long)result[0] == 1,
            Limit: limit.BurstCapacity,
            Remaining: (int)(long)result[1],
            RetryAfter: TimeSpan.FromMilliseconds((long)result[2]),
            ResetAfter: TimeSpan.FromMilliseconds((long)result[3]));
    }

    private static string LoadScript()
    {
        using var stream = typeof(RedisTokenBucket).Assembly.GetManifestResourceStream("Flux.Gateway.RateLimiting.token_bucket.lua")
            ?? throw new InvalidOperationException("token_bucket.lua is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
