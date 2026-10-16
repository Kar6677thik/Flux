using Flux.Gateway.Tenancy;
using Microsoft.Extensions.Options;

namespace Flux.Gateway.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public static readonly IReadOnlyList<string> Algorithms = [InMemoryTokenBucket.Name, InMemoryFixedWindow.Name, RedisTokenBucket.Name];

    /// <summary>Which <see cref="IRateLimiter"/> runs.</summary>
    public string Algorithm { get; set; } = InMemoryTokenBucket.Name;

    public FixedWindowOptions FixedWindow { get; set; } = new();

    public RedisLimiterOptions Redis { get; set; } = new();

    public FallbackOptions Fallback { get; set; } = new();

    /// <summary>What a route group does when Redis can't answer (spec §9.5, ADR-0005).</summary>
    public Dictionary<string, FailurePolicy> FailurePolicy { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [RouteGroups.Payments] = RateLimiting.FailurePolicy.FailOpen,
        [RouteGroups.Refunds] = RateLimiting.FailurePolicy.FailClosed,
    };
}

public sealed class FixedWindowOptions
{
    /// <summary>Window length W (spec §12). Experiment E2 only.</summary>
    public int WindowSeconds { get; set; } = 10;
}

public sealed class RedisLimiterOptions
{
    /// <summary>
    /// Per-call timeout. The client's default is 5,000 ms, and with that a Redis outage adds five
    /// seconds to every request (experiment E3). 50 ms bounds the damage (ADR-0005).
    /// </summary>
    public int TimeoutMs { get; set; } = 50;

    /// <summary>
    /// Wrap Redis in <see cref="ResilientRateLimiter"/> (timeout, circuit breaker, failure policy).
    /// Only experiment E3 turns this off, to show what happens without it.
    /// </summary>
    public bool UseResilience { get; set; } = true;
}

public sealed class FallbackOptions
{
    /// <summary>The fail-open fallback gives each instance limit ÷ this. Keep it equal to the replica count.</summary>
    public int AssumedInstances { get; set; } = 3;
}

public enum FailurePolicy
{
    FailOpen,
    FailClosed,
}

/// <summary>Fail fast (spec §12): every rule here stops the gateway at startup, naming the bad key.</summary>
public sealed class RateLimitingOptionsValidator(IConfiguration configuration) : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions o)
    {
        var errors = new List<string>();
        if (!RateLimitingOptions.Algorithms.Contains(o.Algorithm))
            errors.Add($"RateLimiting:Algorithm must be one of: {string.Join(", ", RateLimitingOptions.Algorithms)}.");
        if (o.FixedWindow.WindowSeconds is < 1 or > 3600)
            errors.Add("RateLimiting:FixedWindow:WindowSeconds must be between 1 and 3600.");
        if (o.Redis.TimeoutMs is < 1 or > 30_000)
            errors.Add("RateLimiting:Redis:TimeoutMs must be between 1 and 30000.");
        if (o.Fallback.AssumedInstances is < 1 or > 1000)
            errors.Add("RateLimiting:Fallback:AssumedInstances must be between 1 and 1000.");
        foreach (var group in RouteGroups.All.Where(g => !o.FailurePolicy.ContainsKey(g)))
            errors.Add($"RateLimiting:FailurePolicy:{group} must be FailOpen or FailClosed.");
        if (o.Algorithm == RedisTokenBucket.Name && string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis")))
            errors.Add("ConnectionStrings:Redis is required when RateLimiting:Algorithm is RedisTokenBucket.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
