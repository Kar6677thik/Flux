using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Flux.Gateway.RateLimiting;

/// <summary>
/// The one Redis connection the gateway shares (a multiplexer pipelines every caller's commands
/// over a single socket). Connecting starts in the background at startup and never blocks it:
/// spec §9.5 requires the gateway to start even when Redis is down.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly Task<ConnectionMultiplexer> _connecting;

    public RedisConnection(IConfiguration configuration, IOptions<RateLimitingOptions> options, ILogger<RedisConnection> logger)
    {
        var redis = options.Value.Redis;
        var config = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required."));

        // Keep retrying in the background instead of throwing when the first connect fails.
        config.AbortOnConnectFail = false;
        config.AsyncTimeout = redis.TimeoutMs;
        config.SyncTimeout = redis.TimeoutMs;
        if (redis.UseResilience)
        {
            // While disconnected, fail each command at once instead of queueing it until the
            // timeout. A dead Redis then costs microseconds, not 50 ms per request.
            config.BacklogPolicy = BacklogPolicy.FailFast;
            config.ConnectTimeout = 1_000;
        }

        Configuration = config.ToString(includePassword: false);
        _connecting = ConnectAsync(config, logger);
    }

    /// <summary>The effective client settings, without any password, for logs and tests.</summary>
    public string Configuration { get; }

    public Task<ConnectionMultiplexer> GetAsync() => _connecting;

    private static async Task<ConnectionMultiplexer> ConnectAsync(ConfigurationOptions config, ILogger logger)
    {
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(config);
        multiplexer.ConnectionFailed += (_, e) => logger.LogWarning("Redis connection lost: {failure}", e.FailureType);
        multiplexer.ConnectionRestored += (_, _) => logger.LogInformation("Redis connection restored");
        if (!multiplexer.IsConnected)
            logger.LogWarning("Redis is not reachable yet; retrying in the background");
        return multiplexer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connecting.IsCompletedSuccessfully)
            await _connecting.Result.DisposeAsync();
    }
}
