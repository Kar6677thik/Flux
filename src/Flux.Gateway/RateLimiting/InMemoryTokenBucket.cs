using System.Collections.Concurrent;
using Flux.Gateway.Tenancy;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// Token bucket (spec §9.2, ADR-0002) held in this process's memory. Each gateway instance has its
/// own buckets, so three instances allow three times the limit. That is the bug experiment E1
/// demonstrates in M4 and M5 fixes with Redis.
/// </summary>
public sealed class InMemoryTokenBucket(TimeProvider time) : IRateLimiter
{
    public const string Name = "InMemoryTokenBucket";
    public static readonly TimeSpan IdleEviction = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<RateLimitKey, Bucket> _buckets = new();
    private long _lastSweep = time.GetTimestamp();

    public string Algorithm => Name;

    public ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct)
    {
        var now = time.GetTimestamp();   // monotonic: wall-clock jumps (NTP, DST) can't add or remove tokens
        SweepIdleBuckets(now);

        var bucket = _buckets.GetOrAdd(key, static (_, state) => new Bucket(state.limit.BurstCapacity, state.now), (limit, now));
        return ValueTask.FromResult(bucket.TryTake(limit, now, time));
    }

    internal int BucketCount => _buckets.Count;

    // An idle bucket has long since refilled, so deleting it changes nothing: the next request
    // creates a full one. That holds while B / r is under 10 minutes, true for every plan we seed.
    private void SweepIdleBuckets(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (time.GetElapsedTime(last, now) < SweepInterval || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
            return;   // not due yet, or another request is already sweeping

        foreach (var (key, bucket) in _buckets)
            if (bucket.IdleFor(now, time) >= IdleEviction)
                _buckets.TryRemove(new KeyValuePair<RateLimitKey, Bucket>(key, bucket));
    }

    private sealed class Bucket(double tokens, long lastRefill)
    {
        // One lock per bucket, not one for the limiter: merchants never wait on each other.
        private readonly Lock _gate = new();
        private double _tokens = tokens;
        private long _lastRefill = lastRefill;

        public TimeSpan IdleFor(long now, TimeProvider time)
        {
            lock (_gate)
                return time.GetElapsedTime(_lastRefill, now);
        }

        public RateLimitDecision TryTake(RouteLimit limit, long now, TimeProvider time)
        {
            lock (_gate)
            {
                // Lazy refill: no timers, just "how much has accrued since we last looked".
                var elapsed = time.GetElapsedTime(_lastRefill, now).TotalSeconds;
                // Rounded to 1e-9 so binary floating point can't leave 2.9999999999 tokens where
                // the maths says 3 and refuse a request that should pass.
                _tokens = Math.Round(Math.Min(limit.BurstCapacity, _tokens + elapsed * limit.RefillPerSecond), 9);
                _lastRefill = now;

                var allowed = _tokens >= 1;
                if (allowed)
                    _tokens -= 1;

                var retryAfter = allowed ? TimeSpan.Zero : CeilingTicks((1 - _tokens) / limit.RefillPerSecond);
                var resetAfter = CeilingTicks((limit.BurstCapacity - _tokens) / limit.RefillPerSecond);
                return new RateLimitDecision(allowed, (int)Math.Floor(_tokens), retryAfter, resetAfter);
            }
        }

        // TimeSpan.FromSeconds truncates: (1 − 0.3) / 10 is 0.0699999… in binary, so it would say
        // 69.9999 ms, and a client waiting exactly that long would arrive one tick early and be
        // refused again. Rounding up to the next tick makes "wait RetryAfter" always enough.
        private static TimeSpan CeilingTicks(double seconds) =>
            TimeSpan.FromTicks((long)Math.Ceiling(Math.Round(seconds * TimeSpan.TicksPerSecond, 3)));
    }
}
