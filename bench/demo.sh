#!/usr/bin/env bash
# A two-minute tour of Flux's behaviour, the one recorded in docs/images/demo.svg.
#
#   FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 \
#     docker compose -f deploy/compose/docker-compose.yml --profile single up -d
#   bench/demo.sh
#
# It stops and starts the Compose Redis (step 3), so run it against a local stack only.
set -uo pipefail

gw=localhost:8080
quiet="Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"   # public dev key, plan pro
noisy="Authorization: Bearer flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM"   # public dev key, plan free (10/s)
compose=(docker compose -f deploy/compose/docker-compose.yml)

say()  { printf '\n\033[1;36m# %s\033[0m\n' "$*"; sleep 1; }
show() { printf '\033[1;32m$\033[0m %s\n' "$1"; sleep 0.4; eval "$1"; }
# Status line, the rate-limit and timing headers, and the body cut to the terminal width.
headers() { grep -iE '^HTTP|x-ratelimit|retry-after|server-timing|^\{' | cut -c1-96; }

# Warm-up, not shown: the first requests after a restart pay JIT and connection set-up, which
# would make Server-Timing in step 1 look nothing like steady state (P3 measured ~0.3 ms).
for i in 1 2 3; do curl -so /dev/null $gw/v1/payments/pay_1 -H "$quiet"; done

say "1. A merchant's request: authenticated, rate-limited, forwarded, timed"
show "curl -si $gw/v1/payments/pay_1 -H \"\$quiet\" | headers"

say "2. noisy-corp (free plan: 2 refunds/s) fires 25 refunds at once"
show "seq 25 | xargs -P 25 -I{} curl -s -o /dev/null -w '%{http_code}\n' $gw/v1/refunds/rfnd_1 -H \"\$noisy\" | sort | uniq -c"
show "for i in 1 2; do curl -so /dev/null $gw/v1/refunds/rfnd_1 -H \"\$noisy\"; done; curl -si $gw/v1/refunds/rfnd_1 -H \"\$noisy\" | headers"

say "3. Redis dies. Payments fail open (degraded); refunds fail closed (503)"
show "${compose[*]} stop redis 2>/dev/null"
show "curl -si $gw/v1/payments/pay_1 -H \"\$quiet\" | headers"
show "curl -si $gw/v1/refunds/rfnd_1 -H \"\$quiet\" | headers"
show "curl -s $gw/metrics | grep '^flux_ratelimiter_fallback_total' | grep -o 'policy=.*'"

say "4. Redis returns; no restart, no human. Back to normal after the breaker's next trial"
show "${compose[*]} start redis 2>/dev/null"
for i in $(seq 1 20); do
  sleep 1
  curl -si $gw/v1/payments/pay_1 -H "$quiet" | grep -qi 'x-ratelimit-degraded' || break
done
show "curl -si $gw/v1/payments/pay_1 -H \"\$quiet\" | headers"
printf '\n'
