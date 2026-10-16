// Experiments E3 and P4 (spec §15): steady traffic while Redis is stopped and started again.
// Run it through bench/run-outage.sh, which stops Redis at +20 s, starts it at +40 s, and
// records both instants so bench/analyze_outage.py can split the run into before/during/after.
//
// quiet-shop (pro: payments 100/s, refunds 20/s) sends 50 payments/s and 10 refunds/s, all well
// under its limits. Any 429, 503, 500 or extra latency is caused by the outage, not by the merchant.
import http from 'k6/http';
import { Trend } from 'k6/metrics';
import { BASE_URL, GATEWAYS, KEYS, auth, waitUntilReady } from './lib/flux.js';

const SECONDS = Number(__ENV.SECONDS || 60);
const LABEL = __ENV.LABEL || 'outage';

// One sample per request, tagged with what happened, so the NDJSON output is a full timeline.
const request = new Trend('flux_req', true);

function scenario(rate, exec) {
  return {
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration: `${SECONDS}s`,
    preAllocatedVUs: 50,
    // With E3's 5 s waits, 60 req/s × 5 s = 300 requests in flight. Without enough VUs k6 would
    // drop iterations and quietly measure less load (coordinated omission).
    maxVUs: 600,
    exec,
  };
}

export const options = {
  scenarios: { payments: scenario(50, 'payments'), refunds: scenario(10, 'refunds') },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  waitUntilReady(GATEWAYS);
}

function send(route, path) {
  const response = http.get(`${BASE_URL}${path}`, { ...auth(KEYS.quiet), tags: { name: route } });
  request.add(response.timings.duration, {
    route,
    status: String(response.status),
    degraded: response.headers['X-Ratelimit-Degraded'] === 'true' ? 'true' : 'false',
  });
}

export function payments() {
  send('payments', '/v1/payments/pay_1');
}

export function refunds() {
  send('refunds', '/v1/refunds/rfnd_1');
}

export function handleSummary(data) {
  return {
    stdout: `${LABEL}: done; dropped iterations ${data.metrics.dropped_iterations ? data.metrics.dropped_iterations.values.count : 0}\n`,
    [`/results/${LABEL}-summary.json`]: JSON.stringify(data, null, 2),
  };
}
