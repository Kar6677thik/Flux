# Testing code that depends on time

> Field Guide: §23.1 Unit Testing, §23.30 Hermetic Tests, §23.31 Flaky Test Investigation, §23.27 Concurrency Testing · Introduced in: M2 (cache TTLs), M3 (token bucket)
> Code: every class that takes a `TimeProvider`: [`InMemoryTokenBucket`](../../src/Flux.Gateway/RateLimiting/InMemoryTokenBucket.cs), [`ApiKeyCache`](../../src/Flux.Gateway/Auth/ApiKeyCache.cs), [`PlanRefreshService`](../../src/Flux.Gateway/Tenancy/PlanRefreshService.cs), [`RequestTelemetryMiddleware`](../../src/Flux.Gateway/Observability/RequestTelemetry.cs)

## Problem
"After 100 ms one token has refilled." "After 60 s a revoked key stops working." If the code reads the real clock (`DateTime.UtcNow`, `Stopwatch`), a test has to *actually wait*. That makes tests slow (60 s for one test), and **flaky**: on a busy CI runner, `Thread.Sleep(100)` might sleep 115 ms, and the test that expected exactly one token sees two. Flaky tests teach people to ignore red builds (§23.31).

## Context
Anything with TTLs, timeouts, retries with backoff, scheduling or rate limits. In Flux that's most of the interesting code. CLAUDE.md makes it a rule: *never sleep on the wall clock; use `FakeTimeProvider`.*

## Solution
Treat time as a **dependency** and inject it, like a database.

```csharp
// production: TimeProvider.System (registered in Program.cs)
public sealed class InMemoryTokenBucket(TimeProvider time) : IRateLimiter
{
    var now = time.GetTimestamp();                // instead of Stopwatch.GetTimestamp()
    var elapsed = time.GetElapsedTime(last, now); // instead of a manual subtraction
}

// test: Microsoft.Extensions.Time.Testing
var time = new FakeTimeProvider();
var limiter = new InMemoryTokenBucket(time);
for (var i = 0; i < 10; i++) Take();   // drain the bucket
time.Advance(TimeSpan.FromMilliseconds(30));
Assert.Equal(TimeSpan.FromMilliseconds(70), Take().RetryAfter);   // exact, every run
```
`TimeProvider` (.NET 8+) covers both kinds of time:
- **Wall-clock time**, `GetUtcNow()`: "what time is it?" Used for expiry moments (the cache's `ExpiresAt`) and `created_at`.
- **Monotonic time**, `GetTimestamp()` / `GetElapsedTime()`: "how long since?" It never jumps when NTP adjusts the clock. Used for elapsed time (refill, durations).

It also covers **timers**: `Task.Delay(span, timeProvider, ct)` and `new PeriodicTimer(span, timeProvider)`. With the fake, they fire only when `Advance` moves past them. That's how `PlanRefreshService` can be time-controlled.

### The same trick at integration level
`GatewayFactory` swaps the *real gateway's* `TimeProvider` for a `FakeTimeProvider`. A test can then drive HTTP → middleware → cache → PostgreSQL, and still say "now it's 60 s later":
```csharp
gateway.Time.Advance(TimeSpan.FromSeconds(59));  → 200 (revoked key still cached)
gateway.Time.Advance(TimeSpan.FromSeconds(1));   → 401
```

## Alternatives
| Option | Trade-off |
|--------|-----------|
| `Thread.Sleep` / `Task.Delay` in tests | Slow and flaky. Banned here. |
| Your own `IClock` interface | What everyone did before .NET 8. `TimeProvider` is the built-in standard, and libraries now accept it. |
| Loose assertions ("between 50 and 150 ms") | Hides real bugs: the 1-tick `RetryAfter` error would pass any tolerance. |
| Test only with real time, using huge margins | You never test the boundary, which is where the bugs live. |

## Trade-offs
- **Everything must take a `TimeProvider`.** One stray `DateTime.UtcNow` and that code escapes the fake. Code review has to watch for it.
- **Frozen time hides real latency.** With the fake, `Server-Timing` reports `0` in tests. Real durations come from the Compose run and from k6 (M4, P3).
- **Not every library takes a clock.** YARP's `ActivityTimeout` uses real timers, so the `504` test uses a short real timeout and a slow upstream instead.

## Implementation notes
- **Exact boundaries become testable, and they find bugs.** M3's `Retry_after_is_the_exact_time_until_one_whole_token` showed `69.9999 ms` where the maths says `70 ms`, which led to rounding durations *up* to the next tick. A sleep-based test could never have seen a 100 ns error.
- **Frozen time makes concurrency tests sharper.** With the clock stopped, no tokens refill during `Concurrent_requests_never_spend_the_same_token_twice`, so the expected answer is *exactly* 100. Any race shows as 101+.

## Testing
This note *is* about testing. See `ApiKeyCacheTests`, `InMemoryTokenBucketTests`, `ApiKeyAuthTests` (TTL boundaries) and `RateLimitContractTests` (refill through HTTP).

## Observability
Not applicable. In production `TimeProvider.System` is just the real clock.

## Related
[token-bucket](token-bucket.md) · [caching-and-negative-caching](caching-and-negative-caching.md) · later: clock-skew (M5, where Redis's clock replaces ours)
