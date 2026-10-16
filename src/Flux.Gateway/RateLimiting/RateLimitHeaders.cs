using System.Globalization;
using Flux.Gateway.Tenancy;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// The header contract from spec §7.3. Times are delta seconds, rounded up: a client that waits
/// the advertised time must find what was promised, so we never round down.
/// </summary>
public static class RateLimitHeaders
{
    public const string Limit = "X-RateLimit-Limit";
    public const string Remaining = "X-RateLimit-Remaining";
    public const string Reset = "X-RateLimit-Reset";
    public const string Degraded = "X-RateLimit-Degraded";

    public static void Apply(IHeaderDictionary headers, RateLimitDecision decision, RouteLimit limit)
    {
        headers[Limit] = limit.BurstCapacity.ToString(CultureInfo.InvariantCulture);
        headers[Remaining] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        headers[Reset] = CeilingSeconds(decision.ResetAfter).ToString(CultureInfo.InvariantCulture);
        if (!decision.Allowed)
            headers.RetryAfter = Math.Max(1, CeilingSeconds(decision.RetryAfter)).ToString(CultureInfo.InvariantCulture);
        if (decision.Source == DecisionSource.Fallback)
            headers[Degraded] = "true";
    }

    // Tolerates the last few ticks of floating-point noise: 1.0000000001 s is "1", not "2".
    internal static long CeilingSeconds(TimeSpan value) => (long)Math.Ceiling(Math.Round(value.TotalSeconds, 6));
}
