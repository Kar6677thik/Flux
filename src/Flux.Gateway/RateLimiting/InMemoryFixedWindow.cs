using System.Collections.Concurrent;
using Flux.Gateway.Tenancy;
using Microsoft.Extensions.Options;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// Fixed-window counter (spec §9.3). It is DELIBERATELY FLAWED and exists only for experiment E2:
/// windows are calendar slots, so a client can spend a full quota just before a boundary and
/// another full quota just after it. That's 2× the quota within moments. Never select it for real traffic.
/// </summary>
public sealed class InMemoryFixedWindow(TimeProvider time, IOptions<RateLimitingOptions> options) : IRateLimiter
{
    public const string Name = "InMemoryFixedWindow";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly int _windowSeconds = options.Value.FixedWindow.WindowSeconds;
    private readonly ConcurrentDictionary<RateLimitKey, Window> _windows = new();
    private long _lastSweepMs = time.GetUtcNow().ToUnixTimeMilliseconds();

    public string Algorithm => Name;

    public ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct)
    {
        // Wall-clock time on purpose: a fixed window is "the 10 seconds starting at :00, :10, :20…",
        // aligned to the epoch. That alignment is precisely what makes the boundary predictable,
        // and therefore exploitable.
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = _windowSeconds * 1000L;
        var index = nowMs / windowMs;
        SweepOldWindows(nowMs, index);

        // limit = r × W (spec §9.3): free payments at 10/s with W = 10 s is 100 per window.
        var quota = Math.Max(1, (int)Math.Round(limit.RefillPerSecond * _windowSeconds));
        var window = _windows.GetOrAdd(key, static _ => new Window());
        var untilNext = TimeSpan.FromMilliseconds((index + 1) * windowMs - nowMs);

        lock (window.Gate)
        {
            if (window.Index != index)
            {
                window.Index = index;   // a new slot: the count simply starts again from zero
                window.Count = 0;
            }

            var allowed = window.Count < quota;
            if (allowed)
                window.Count++;

            return ValueTask.FromResult(new RateLimitDecision(
                allowed, quota, quota - window.Count, allowed ? TimeSpan.Zero : untilNext, untilNext));
        }
    }

    internal int WindowCount => _windows.Count;

    private void SweepOldWindows(long nowMs, long currentIndex)
    {
        var last = Interlocked.Read(ref _lastSweepMs);
        if (nowMs - last < SweepInterval.TotalMilliseconds || Interlocked.CompareExchange(ref _lastSweepMs, nowMs, last) != last)
            return;

        foreach (var (key, window) in _windows)
            if (Volatile.Read(ref window.Index) < currentIndex)
                _windows.TryRemove(new KeyValuePair<RateLimitKey, Window>(key, window));
    }

    private sealed class Window
    {
        public readonly Lock Gate = new();
        public long Index = -1;
        public int Count;
    }
}
