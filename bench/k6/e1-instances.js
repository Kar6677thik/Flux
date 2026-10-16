// Experiment E1 (spec §15): per-instance in-memory buckets behind a round-robin balancer.
// noisy-corp (free: 10/s, burst 10) offers 100 req/s for 60 s. One correct shared limiter would
// allow about B + r·T = 10 + 10 × 60 = 610. Each gateway has its own bucket, so we expect ~3×.
//
//   docker compose -f deploy/compose/docker-compose.yml --profile bench run --rm k6 run /scripts/e1-instances.js
//   (single-gateway baseline: add  -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e LABEL=e1-single)
import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { BASE_URL, GATEWAYS, KEYS, auth, count, requestCounts, waitUntilReady } from './lib/flux.js';

const RATE = Number(__ENV.RATE || 100);
const SECONDS = Number(__ENV.SECONDS || 60);
const LABEL = __ENV.LABEL || 'e1-cluster';

const allowed = new Counter('flux_allowed');
const limited = new Counter('flux_limited');
const other = new Counter('flux_other_status');
// One counter per gateway, filled from each gateway's own /metrics in teardown.
const perGateway = Object.fromEntries(GATEWAYS.map((g) => [g, new Counter(`allowed_by_${g.replace(/-/g, '_')}`)]));

export const options = {
  scenarios: {
    noisy: {
      // Constant *arrival* rate: requests start on schedule whether or not earlier ones have
      // answered. A closed loop ("send, wait, send") would slow down with the server and hide
      // latency (coordinated omission, spec §15).
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: `${SECONDS}s`,
      preAllocatedVUs: 50,
      maxVUs: 200,
    },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  waitUntilReady(GATEWAYS);
}

export default function () {
  const response = http.get(`${BASE_URL}/v1/payments/pay_1`, { ...auth(KEYS.noisy), tags: { name: 'payments-get' } });
  if (response.status === 200) allowed.add(1);
  else if (response.status === 429) limited.add(1);
  else other.add(1, { status: String(response.status) });
}

export function teardown() {
  for (const gateway of GATEWAYS) perGateway[gateway].add(requestCounts(gateway, 'payments').allowed || 0);
}

export function handleSummary(data) {
  const a = count(data, 'flux_allowed');
  const l = count(data, 'flux_limited');
  const bound = 10 + 10 * SECONDS;
  const lines = [
    `${LABEL}: offered ${RATE} req/s for ${SECONDS}s against ${BASE_URL}`,
    `  sent ${a + l + count(data, 'flux_other_status')} | allowed ${a} | limited ${l} | other ${count(data, 'flux_other_status')}`,
    `  dropped iterations (k6 couldn't start on time): ${count(data, 'dropped_iterations')}`,
    `  single-limiter bound B + r*T = ${bound} -> allowed / bound = ${(a / bound).toFixed(2)}x`,
    ...GATEWAYS.map((g) => `  ${g}: allowed ${count(data, `allowed_by_${g.replace(/-/g, '_')}`)} (from its /metrics)`),
    `  latency p50 ${data.metrics.http_req_duration.values['p(50)'].toFixed(2)} ms, p99 ${data.metrics.http_req_duration.values['p(99)'].toFixed(2)} ms`,
    '',
  ];
  return { stdout: lines.join('\n'), [`/results/${LABEL}.json`]: JSON.stringify(data, null, 2) };
}
