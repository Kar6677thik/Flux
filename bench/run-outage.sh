#!/usr/bin/env bash
# Runs bench/k6/redis-outage.js while breaking Redis on a schedule, then analyses the timeline.
#
#   bench/run-outage.sh <label> [stop|pause]
#
# stop   docker stop: Redis closes its connections, so the failure is noticed at once (default)
# pause  docker pause: Redis freezes but its connections stay open. Nothing answers and nothing
#        errors, the "slow failure" that only a timeout can detect.
#
# The gateway must already be running with the settings under test, e.g. for P4:
#   FLUX_ALGORITHM=RedisTokenBucket FLUX_ASSUMED_INSTANCES=1 docker compose -f deploy/compose/docker-compose.yml --profile single up -d
set -euo pipefail

label="${1:?usage: bench/run-outage.sh <label> [stop|pause]}"
mode="${2:-stop}"
compose=(docker compose -f deploy/compose/docker-compose.yml)
results=bench/results
mkdir -p "$results"
events="$results/$label-events.txt"
now_ms() { python -c 'import time; print(int(time.time() * 1000))'; }

# Git Bash would otherwise rewrite /scripts/… and /results/… into Windows paths.
export MSYS_NO_PATHCONV=1

"${compose[@]}" --profile bench run --rm k6 run --quiet \
  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL="$label" \
  --out "json=/results/$label.ndjson" /scripts/redis-outage.js &
k6_pid=$!

sleep 20
echo "break $(now_ms) $mode" > "$events"
if [ "$mode" = pause ]; then "${compose[@]}" pause redis; else "${compose[@]}" stop redis; fi

sleep 20
echo "heal $(now_ms) $mode" >> "$events"
if [ "$mode" = pause ]; then "${compose[@]}" unpause redis; else "${compose[@]}" start redis; fi

wait "$k6_pid"
python -I bench/analyze_outage.py "$results/$label.ndjson" "$events"
