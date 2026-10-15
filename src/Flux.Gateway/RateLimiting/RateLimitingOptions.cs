using System.ComponentModel.DataAnnotations;

namespace Flux.Gateway.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Which <see cref="IRateLimiter"/> runs. M4 adds InMemoryFixedWindow, M5 RedisTokenBucket.</summary>
    [Required]
    [RegularExpression("^InMemoryTokenBucket$", ErrorMessage = "RateLimiting:Algorithm must be 'InMemoryTokenBucket'.")]
    public string Algorithm { get; set; } = InMemoryTokenBucket.Name;
}
