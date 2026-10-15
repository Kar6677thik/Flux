using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Flux.Gateway.Data;

/// <summary>
/// Used only by `dotnet ef` (migrations add, bundle). Without it the tooling would boot the whole
/// gateway just to discover the model. Creating a migration never connects, so the connection
/// string here is a placeholder, not a secret.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FluxDbContext>
{
    public FluxDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<FluxDbContext>()
        .UseNpgsql("Host=design-time-only")
        .UseSnakeCaseNamingConvention()
        .Options);
}
