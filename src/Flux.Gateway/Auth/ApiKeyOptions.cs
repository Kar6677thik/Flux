using System.ComponentModel.DataAnnotations;

namespace Flux.Gateway.Auth;

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";

    /// <summary>How long a found key is trusted without asking PostgreSQL. Also the worst-case delay for a revocation.</summary>
    [Range(1, 3600)]
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>How long "no such key" is remembered, so floods of random keys don't reach the database.</summary>
    [Range(1, 3600)]
    public int NegativeCacheTtlSeconds { get; set; } = 10;

    /// <summary>Upper bound on cached entries, so a flood of distinct random keys can't exhaust memory.</summary>
    [Range(1, 10_000_000)]
    public int MaxCacheEntries { get; set; } = 100_000;
}
