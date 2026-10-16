// Experiment E2 (spec §15): the fixed window's boundary burst.
// One gateway with InMemoryFixedWindow (W = 10 s, so free payments get 100 per window).
// 100 requests just before a window boundary and 100 just after: we expect ~200 allowed within a
// few hundred ms, double the quota. Run it again with InMemoryTokenBucket to compare.
//
//   FLUX_ALGORITHM=InMemoryFixedWindow docker compose … --profile single up -d
//   docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run \
//     -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e2-fixed-window /scripts/e2-boundary.js
import http from 'k6/http';
import { sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { BASE_URL, GATEWAYS, KEYS, auth, count, waitUntilReady } from './lib/flux.js';

const WINDOW_MS = Number(__ENV.WINDOW_SECONDS || 10) * 1000;
const PER_BATCH = Number(__ENV.PER_BATCH || 100);
const LEAD_MS = Number(__ENV.LEAD_MS || 150);   // first batch starts this long before the boundary
const LAG_MS = Number(__ENV.LAG_MS || 50);      // second batch starts this long after it
const LABEL = __ENV.LABEL || 'e2';

const allowedBefore = new Counter('allowed_before_boundary_batch');
const allowedAfter = new Counter('allowed_after_boundary_batch');
const span = new Trend('burst_span_ms');                  // first request sent → last response received
const firstBatchEnd = new Trend('first_batch_end_vs_boundary_ms');

export const options = {
  scenarios: { boundary: { executor: 'per-vu-iterations', vus: 1, iterations: 1, maxDuration: '2m' } },
  batch: PER_BATCH,           // k6 defaults to 20 parallel requests per batch …
  batchPerHost: PER_BATCH,    // … and 6 per host; a burst needs them all in flight at once
};

export function setup() {
  waitUntilReady(GATEWAYS);
}

function burst() {
  const request = ['GET', `${BASE_URL}/v1/payments/pay_1`, null, { ...auth(KEYS.noisy), tags: { name: 'payments-get' } }];
  return http.batch(Array.from({ length: PER_BATCH }, () => request)).filter((r) => r.status === 200).length;
}

export default function () {
  // Windows are aligned to the Unix epoch. k6 and the gateway share the Docker VM's clock, so
  // "the boundary" is the same instant for both. Leave at least 2 s to get into position.
  const now = Date.now();
  let boundary = (Math.floor(now / WINDOW_MS) + 1) * WINDOW_MS;
  if (boundary - now < 2000) boundary += WINDOW_MS;

  sleep((boundary - LEAD_MS - Date.now()) / 1000);
  const start = Date.now();
  allowedBefore.add(burst());
  firstBatchEnd.add(Date.now() - boundary);

  sleep(Math.max(0, boundary + LAG_MS - Date.now()) / 1000);
  allowedAfter.add(burst());
  span.add(Date.now() - start);
}

export function handleSummary(data) {
  const before = count(data, 'allowed_before_boundary_batch');
  const after = count(data, 'allowed_after_boundary_batch');
  const lines = [
    `${LABEL}: ${PER_BATCH} requests starting ${LEAD_MS} ms before a ${WINDOW_MS / 1000}s boundary, ${PER_BATCH} more ${LAG_MS} ms after`,
    `  allowed: first batch ${before}, second batch ${after}, total ${before + after} of ${2 * PER_BATCH}`,
    `  first batch finished ${data.metrics.first_batch_end_vs_boundary_ms.values.avg.toFixed(0)} ms relative to the boundary`,
    `  whole burst took ${data.metrics.burst_span_ms.values.avg.toFixed(0)} ms (first request sent -> last response)`,
    '',
  ];
  return { stdout: lines.join('\n'), [`/results/${LABEL}.json`]: JSON.stringify(data, null, 2) };
}
