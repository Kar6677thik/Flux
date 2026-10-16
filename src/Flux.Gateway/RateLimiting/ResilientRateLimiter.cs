using Flux.Gateway.Observability;
using Flux.Gateway.Tenancy;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using StackExchange.Redis;

namespace Flux.Gateway.RateLimiting;

/// <summary>Thrown when Redis can't answer and the route group fails closed (spec §9.5).</summary>
public sealed class RateLimiterUnavailableException(string routeGroup, Exception inner)
    : Exception($"Rate limiter unavailable for route group '{routeGroup}'.", inner);

/// <summary>
/// Decorator around the Redis limiter (spec §9.5, ADR-0005, ADR-0010). Callers still just see an
/// IRateLimiter, but a slow or dead Redis no longer turns into slow or failed requests:
///   timeout (50 ms) → circuit breaker (≥ 50% of ≥ 20 calls in 10 s → open for 5 s) → per-route policy.
/// payments fail open to a local bucket sized limit ÷ AssumedInstances; refunds fail closed (503).
/// </summary>
public sealed class ResilientRateLimiter : IRateLimiter
{
    public const double FailureRatio = 0.5;
    public const int MinimumThroughput = 20;
    public static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    private readonly IRateLimiter _primary;
    private readonly InMemoryTokenBucket _fallback;
    private readonly RateLimitingOptions _options;
    private readonly FluxMetrics _metrics;
    private readonly ILogger<ResilientRateLimiter> _logger;
    private readonly CircuitBreakerStateProvider _circuit = new();
    private readonly ResiliencePipeline _pipeline;

    public ResilientRateLimiter(IRateLimiter primary, IOptions<RateLimitingOptions> options, TimeProvider time,
        FluxMetrics metrics, ILogger<ResilientRateLimiter> logger)
    {
        _primary = primary;
        _fallback = new InMemoryTokenBucket(time);
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
        metrics.ObserveCircuitState(() => _circuit.CircuitState);

        _pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            // Outermost: the breaker counts timeouts as failures too.
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = FailureRatio,
                MinimumThroughput = MinimumThroughput,
                SamplingDuration = SamplingDuration,
                BreakDuration = BreakDuration,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && IsRedisFailure(ex, args.Context.CancellationToken)),
                StateProvider = _circuit,
                OnOpened = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Rate limiter circuit OPEN for {seconds} s: Redis calls are failing", args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    Interlocked.Increment(ref _trialCalls);
                    logger.LogInformation("Rate limiter circuit HALF-OPEN: letting one trial call through to Redis");
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Rate limiter circuit CLOSED: Redis is answering again");
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMilliseconds(_options.Redis.TimeoutMs))
            .Build();
    }

    public string Algorithm => _primary.Algorithm;

    public CircuitState CircuitState => _circuit.CircuitState;

    private int _trialCalls;

    /// <summary>How many half-open trial calls have been let through. Polly only reports "half-open"
    /// once a call actually arrives after the break, so this is how tests observe it.</summary>
    internal int TrialCalls => Volatile.Read(ref _trialCalls);

    public async ValueTask<RateLimitDecision> TryAcquireAsync(RateLimitKey key, RouteLimit limit, CancellationToken ct)
    {
        try
        {
            return await _pipeline.ExecuteAsync(static async (state, token) =>
                // WaitAsync makes the call abandonable: the Redis client doesn't observe the
                // token itself, so without it the Polly timeout couldn't stop the wait.
                await state.Primary.TryAcquireAsync(state.Key, state.Limit, token).AsTask().WaitAsync(token),
                (Primary: _primary, Key: key, Limit: limit), ct);
        }
        catch (Exception ex) when (ex is BrokenCircuitException || IsRedisFailure(ex, ct))
        {
            return await DegradeAsync(key, limit, ex, ct);
        }
    }

    // A cancellation is normally "the client went away", and that must propagate. But the Redis
    // client also cancels in-flight commands itself when it abandons a dead connection; P4's
    // `docker pause` run showed two of those surfacing as 500s. If *we* didn't ask to cancel,
    // it's Redis failing.
    internal static bool IsRedisFailure(Exception ex, CancellationToken callerToken) => ex switch
    {
        RedisException or TimeoutException or TimeoutRejectedException => true,
        OperationCanceledException => !callerToken.IsCancellationRequested,
        _ => false,
    };

    private async ValueTask<RateLimitDecision> DegradeAsync(RateLimitKey key, RouteLimit limit, Exception cause, CancellationToken ct)
    {
        var policy = _options.FailurePolicy[key.RouteGroup];
        _metrics.LimiterFallback(key.RouteGroup, policy);
        // Debug, not Warning: during an outage this runs on every request. The breaker's own
        // transitions are the Warning-level signal.
        _logger.LogDebug("Redis unavailable ({cause}); {routeGroup} fails {policy}", cause.GetType().Name, key.RouteGroup, policy);

        if (policy == FailurePolicy.FailClosed)
            throw new RateLimiterUnavailableException(key.RouteGroup, cause);

        // "Open" isn't "unlimited": each instance enforces its share of the limit, so all of them
        // together stay near the real quota (ADR-0005).
        var instances = _options.Fallback.AssumedInstances;
        var share = new RouteLimit(limit.RefillPerSecond / instances, Math.Max(1, (int)Math.Ceiling((double)limit.BurstCapacity / instances)));
        var decision = await _fallback.TryAcquireAsync(key, share, ct);
        return decision with { Source = DecisionSource.Fallback };
    }
}
