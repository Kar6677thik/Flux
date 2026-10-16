using Flux.Gateway.Tenancy;

namespace Flux.Gateway.RateLimiting;

/// <summary>One bucket per merchant per route group (spec §5): limits are per merchant, never per key.</summary>
public readonly record struct RateLimitKey(Guid MerchantId, string RouteGroup);

public enum DecisionSource
{
    Primary,
    Fallback,   // M5: the local limiter decided because Redis couldn't
}

/// <summary>
/// The outcome of one admission check. Durations are exact; rounding to whole seconds happens only
/// when they become headers (<see cref="RateLimitHeaders"/>). Limit is the algorithm's own idea of
/// "how many at most": the bucket capacity for a token bucket, the per-window quota for a fixed window.
/// </summary>
public sealed record RateLimitDecision(
    bool Allowed,
    int Limit,
    int Remaining,
    TimeSpan RetryAfter,
    TimeSpan ResetAfter,
    DecisionSource Source = DecisionSource.Primary);

/// <summary>
/// The Strategy seam (spec §9.1): the middleware asks "may this request proceed?" and never knows
/// which algorithm answers. M4 adds a fixed window, M5 a Redis bucket, without touching callers.
/// </summary>
public interface IRateLimiter
{
    /// <summary>Name used in config (RateLimiting:Algorithm) and as the metrics label.</summary>
    string Algorithm { get; }

    ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct);
}
