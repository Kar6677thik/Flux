// Experiment P2 (spec §15, N3): does a flooding merchant hurt a quiet one?
// quiet-shop (pro) sends 20 req/s throughout. With NOISY_RATE > 0, noisy-corp (free: 10/s)
// floods the same route group at that rate. 1000 req/s is 100× its limit.
// Compare quiet-shop's latency between a run with NOISY_RATE=0 and one with NOISY_RATE=1000.
//
//   docker compose … --profile bench run --rm k6 run --quiet \
//     -e BASE_URL=http://gateway:8080 -e GATEWAYS=gateway -e NOISY_RATE=1000 -e LABEL=p2-noisy /scripts/p2-noisy-neighbour.js
import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';
import { BASE_URL, GATEWAYS, KEYS, auth, count, waitUntilReady } from './lib/flux.js';

const SECONDS = Number(__ENV.SECONDS || 60);
const QUIET_RATE = Number(__ENV.QUIET_RATE || 20);
const NOISY_RATE = Number(__ENV.NOISY_RATE || 0);
const LABEL = __ENV.LABEL || `p2-noisy${NOISY_RATE}`;

const quietLatency = new Trend('quiet_latency', true);
const quietOk = new Counter('quiet_ok');
const quietNotOk = new Counter('quiet_not_ok');
const noisyOk = new Counter('noisy_ok');
const noisyLimited = new Counter('noisy_limited');
const noisyOther = new Counter('noisy_other');

function arrival(rate, exec, maxVUs) {
  return { executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration: `${SECONDS}s`, preAllocatedVUs: Math.min(maxVUs, 100), maxVUs, exec };
}

const scenarios = { quiet: arrival(QUIET_RATE, 'quiet', 50) };
if (NOISY_RATE > 0) scenarios.noisy = arrival(NOISY_RATE, 'noisy', 400);

export const options = { scenarios, summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'] };

export function setup() {
  waitUntilReady(GATEWAYS);
}

export function quiet() {
  const response = http.get(`${BASE_URL}/v1/payments/pay_1`, { ...auth(KEYS.quiet), tags: { name: 'quiet' } });
  quietLatency.add(response.timings.duration);
  (response.status === 200 ? quietOk : quietNotOk).add(1);
}

export function noisy() {
  const response = http.get(`${BASE_URL}/v1/payments/pay_1`, { ...auth(KEYS.noisy), tags: { name: 'noisy' } });
  if (response.status === 200) noisyOk.add(1);
  else if (response.status === 429) noisyLimited.add(1);
  else noisyOther.add(1);
}

export function handleSummary(data) {
  const q = data.metrics.quiet_latency.values;
  const lines = [
    `${LABEL}: quiet-shop ${QUIET_RATE} req/s, noisy-corp ${NOISY_RATE} req/s, ${SECONDS}s`,
    `  quiet: ok ${count(data, 'quiet_ok')} not-ok ${count(data, 'quiet_not_ok')} | p50 ${q['p(50)'].toFixed(2)} p95 ${q['p(95)'].toFixed(2)} p99 ${q['p(99)'].toFixed(2)} max ${q.max.toFixed(2)} ms`,
    `  noisy: 200 ${count(data, 'noisy_ok')} | 429 ${count(data, 'noisy_limited')} | other ${count(data, 'noisy_other')}`,
    `  dropped iterations: ${count(data, 'dropped_iterations')}`,
    '',
  ];
  return { stdout: lines.join('\n'), [`/results/${LABEL}.json`]: JSON.stringify(data, null, 2) };
}
