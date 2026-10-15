namespace Flux.Gateway.Tenancy;

/// <summary>
/// Loads the plan catalog at startup and every 60 s after that. Until the first load succeeds it
/// retries every 2 s, and the gateway stays unready, so a gateway that starts before PostgreSQL
/// recovers on its own instead of crashing.
/// </summary>
public sealed class PlanRefreshService(PlanCatalog catalog, TimeProvider time, ILogger<PlanRefreshService> logger) : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await catalog.ReloadAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep serving the last good snapshot; a failed refresh must not take plans away.
                logger.LogWarning(ex, "Plan catalog refresh failed");
            }

            try
            {
                await Task.Delay(catalog.IsLoaded ? RefreshInterval : RetryInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
