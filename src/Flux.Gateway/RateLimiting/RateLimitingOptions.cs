namespace Flux.Gateway.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public static readonly IReadOnlyList<string> Algorithms = [InMemoryTokenBucket.Name, InMemoryFixedWindow.Name];

    /// <summary>Which <see cref="IRateLimiter"/> runs. M5 adds RedisTokenBucket.</summary>
    public string Algorithm { get; set; } = InMemoryTokenBucket.Name;

    public FixedWindowOptions FixedWindow { get; set; } = new();

    public bool IsValid(out string error)
    {
        error = !Algorithms.Contains(Algorithm)
            ? $"RateLimiting:Algorithm must be one of: {string.Join(", ", Algorithms)}."
            : FixedWindow.WindowSeconds is < 1 or > 3600
                ? "RateLimiting:FixedWindow:WindowSeconds must be between 1 and 3600."
                : "";
        return error.Length == 0;
    }
}

public sealed class FixedWindowOptions
{
    /// <summary>Window length W (spec §12). Experiment E2 only.</summary>
    public int WindowSeconds { get; set; } = 10;
}
