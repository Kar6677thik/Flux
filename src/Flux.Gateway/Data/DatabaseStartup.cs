using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flux.Gateway.Data;

public static class DatabaseStartup
{
    /// <summary>
    /// Runs before the gateway accepts traffic. The environment check comes first and is not
    /// optional: the dev keys are published in this repo, so seeding them in Production would hand
    /// anyone a working key.
    /// </summary>
    public static async Task RunAsync(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (options.SeedDevData && !app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Database:SeedDevData is enabled in the '{app.Environment.EnvironmentName}' environment. " +
                "The dev keys are public; they may only be seeded in Development.");
        }

        if (!options.MigrateOnStartup && !options.SeedDevData)
            return;

        var factory = app.Services.GetRequiredService<IDbContextFactory<FluxDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (options.MigrateOnStartup)
            await db.Database.MigrateAsync();
        if (options.SeedDevData)
            await DevSeed.ApplyAsync(db, app.Services.GetRequiredService<TimeProvider>(), CancellationToken.None);
    }
}
