namespace Flux.Gateway.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Apply EF Core migrations when the gateway starts. Convenient locally; off in production, where
    /// a separate Job migrates once before the rollout so three replicas never race (spec §17).
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Create the demo merchants and their public dev keys. Development only.</summary>
    public bool SeedDevData { get; set; }
}
