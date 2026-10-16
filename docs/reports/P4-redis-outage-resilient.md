# P4 — With timeout, breaker and per-route policy, a Redis outage costs payments nothing and refunds a fast 503

- **Phase:** Prove
- **Date:** 2026-10-16
- **Milestone:** M5
- **Code:** branch `m5-redis-resilience` (on top of M4, `37cda01`), with the M5 changes in this PR. Runs 3–5 include the cancellation fix found by run 2.
- **Scripts:** [`bench/k6/redis-outage.js`](../../bench/k6/redis-outage.js), [`bench/run-outage.sh`](../../bench/run-outage.sh), [`bench/analyze_outage.py`](../../bench/analyze_outage.py)

## Hypothesis (spec N4)
With `ResilientRateLimiter` (50 ms timeout, circuit breaker, fail-fast client), during a Redis outage:
- no request waits more than **~50 ms extra**;
- **payments keep flowing** in degraded mode (`X-RateLimit-Degraded: true`);
- **refunds get `503 rate_limiter_unavailable` in < 100 ms**;
- **recovery is automatic** once Redis returns.

This should hold for both kinds of outage:
- **stop**: Redis closes its connections, so the failure is noticed at once;
- **pause**: Redis freezes with its connections still open, and only a timeout can notice.

## Setup
Identical to [E3](E3-redis-outage-default-timeouts.md#setup), except for the gateway settings:

| | E3 | **P4** |
|---|---|---|
| `RateLimiting:Redis:UseResilience` | false | **true** (default) |
| `RateLimiting:Redis:TimeoutMs` | 5000 | **50** (default) |
| Client while disconnected | queue commands | **fail fast** |
| Breaker | none | ≥ 50% of ≥ 20 calls in 10 s → open 5 s |
| `payments` / `refunds` policy | none (500) | **FailOpen** / **FailClosed** |
| `RateLimiting:Fallback:AssumedInstances` | 1 | **1**: one gateway, so its local share is the whole plan |

## Machine
As for E3: Intel Core Ultra 5 225H (14 cores), 14.9 GiB, Windows 11 Pro 10.0.26100, Docker Desktop 28.4.0 / WSL2, `grafana/k6:2.3.0` (2 CPUs), Redis 7.4.11, StackExchange.Redis 3.4.5, Polly.Core 8.8.0, .NET 10.0.401.

## Exact commands
```bash
FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 \
  docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
# before each run: restart the gateway, then one warm-up request
docker compose -f deploy/compose/docker-compose.yml --profile single restart gateway
curl -s -o /dev/null localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"
bench/run-outage.sh p4-stop stop            # run 1
bench/run-outage.sh p4-pause pause          # run 2  → found a bug, fixed, image rebuilt (`up --build -d`)
bench/run-outage.sh p4-pause-fixed-1 pause  # run 3
bench/run-outage.sh p4-pause-fixed-2 pause  # run 4
bench/run-outage.sh p4-stop-fixed stop      # run 5
```

## Results
All five runs: **0 dropped iterations** (E3: 240), so k6 applied the full 60 req/s throughout.

**"During the outage"**, by request send time. Every payment was a `200`; the few refund `200`s were sent in the ~0.45 s before `docker stop`/`pause` took effect.

| Run | Fault | Fix? | Payments | Degraded | Payments p50 / p99 / **max** | Refunds | Refunds p50 / p99 / **max** |
|-----|-------|------|----------|----------|------------------------------|---------|------------------------------|
| 1 | stop | – | 200×1049 | 1026 | 7.57 / 13.15 / **38.28** ms | 200×5 · 503×205 | 1.12 / 10.95 / **22.87** ms |
| 2 | pause | – | 200×1023 · **500×2** | 1007 | 7.54 / 60.10 / **74.37** ms | 200×4 · 503×201 | 1.14 / 53.41 / **81.26** ms |
| 3 | pause | ✅ | 200×1032 | 1010 | 10.05 / 61.45 / **74.37** ms | 200×5 · 503×202 | 1.18 / 55.63 / **83.25** ms |
| 4 | pause | ✅ | 200×1032 | 1010 | 7.95 / 63.34 / **79.95** ms | 200×5 · 503×202 | 1.25 / 56.49 / **84.72** ms |
| 5 | stop | ✅ | 200×1046 | 1024 | 10.02 / 12.69 / **22.22** ms | 200×4 · 503×205 | 1.12 / 10.19 / **23.79** ms |

Baseline "before" phase for comparison: payments p50 8–10.6 ms, p99 12.4–14.8 ms.

**Recovery**, measured from Redis's return to the first normal (non-degraded `200`) response:

| Run | 1 | 2 | 3 | 4 | 5 |
|-----|---|---|---|---|---|
| payments | 4.13 s | 4.48 s | 4.52 s | 4.56 s | 4.10 s |
| refunds | 4.15 s | 4.52 s | 4.57 s | 4.58 s | 4.12 s |

**Timeline of run 4 (pause)**, first seconds of the outage:

| t | payments | refunds | p99 |
|---|----------|---------|-----|
| +0 s | 200×50 (28 degraded) | 200×5, 503×5 | 80.0 / 84.7 ms |
| +1 s | 200×50 (all degraded) | 503×10 | 68.3 / 60.2 ms |
| +2 s onwards | 200×50 (all degraded) | 503×10 | 13.3 / 5.7 ms |

## What the numbers say
1. **Payments never stopped.** Every payment request during every outage got a `200`, marked degraded. Under `stop`, its latency was indistinguishable from normal (p99 12.7–13.2 ms against 12.4–14.8 ms before). The client failed fast, and the breaker took Redis out of the path.
2. **Refunds failed closed, fast.** `503` with a p50 of about 1.1 ms. That's *faster* than a normal request, because nothing is forwarded. The worst refund took 84.72 ms (run 4, pause). **N4's "< 100 ms" holds in every run.**
3. **`pause` is the hard case, and the timeout is what handles it.**
   - For the first ~1–2 s, before the breaker had seen its 20 failures, each request waited out the **50 ms timeout**. The worst requests took 74–85 ms in total: the timeout plus a normal ~10 ms request plus scheduling noise. N4 says "no request waits more than **~50 ms extra**"; the measured worst case is about 60–75 ms over the baseline p50. Close, but over 50 ms at the very tail, and we report it as such.
   - Once the breaker opened (t +2 s), even paused-Redis latency returned to normal.
4. **Run 2 found a real bug.** Two payments got `500`s. StackExchange.Redis 3.x, abandoning the frozen connection, failed two in-flight commands with a `TaskCanceledException`. Our filter deliberately let cancellations through, because a cancellation normally means "the client hung up". The fix treats a cancellation *the caller didn't ask for* as a Redis failure, in both the breaker's `ShouldHandle` and the fallback filter, with unit tests in `ResilientRateLimiterTests`. Runs 3–5, after the fix, had **zero `500`s**.
5. **Recovery was automatic, in 4.1–4.6 s.** No restart and no manual step. E3's timeline suggests the client itself needs about 4 s to reconnect after a 20 s outage, so the client's reconnect, rather than the 5 s break, probably dominates. Untested guess: a shorter break wouldn't help much. During those seconds payments stayed degraded and refunds stayed `503`. That's the "after" phase's 41–45 refund `503`s.
6. **No replay burst.** E3's post-recovery `429` storm (138 + 30 for a merchant within its plan) is gone. Fail-fast means nothing is queued, so nothing is replayed.

## Conclusion
N4 is **met**, with one caveat stated precisely: under a silent (`pause`) failure, the tail of the first ~2 s reaches 60–75 ms over normal, rather than ≤ 50 ms. Payments flowed throughout, refunds failed closed in < 100 ms, recovery took about 4.5 s and needed no human, and the run surfaced and fixed a real cancellation-handling bug. Compared with E3:

| | E3 (defaults) | P4 (resilient, after fix) |
|---|---|---|
| Payments during outage | 88–89% `500`, p50 ~5.5 s | **100% `200`** (degraded), p50 8–10 ms |
| Refunds during outage | ~90% `500`, p50 ~5.5 s | **`503` in ~1.1 ms** (p50) |
| After recovery | 138 + 30 false `429`s | none |
| k6 dropped iterations | 240 | 0 |

## Caveats
- One gateway with `AssumedInstances = 1`. With three gateways, each fails open to a third of the plan. That's correct, but untested here, and P1 (M6) runs the cluster.
- quiet-shop stays under its plan, so this shows *availability*, not how well the fallback *limits*. The integration test `With_redis_down_payments_fail_open_to_a_local_share_of_the_limit` covers that (4 of 6 allowed for a free merchant at ÷3).
- The client's ~4 s reconnect is inferred from the timelines, not instrumented.
