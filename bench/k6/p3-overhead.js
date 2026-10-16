// Experiment P3 (spec §15, N1): how much latency does the gateway itself add?
// Two independent measurements of the same thing:
//   1. TARGET=direct  k6 → payments backend          vs  TARGET=gateway  k6 → gateway → payments
//      (overhead ≈ the difference between the two latency distributions)
//   2. Server-Timing on every gateway response: gw;dur (time spent in the gateway) per request
// Load: 500 req/s for 60 s. Ten pro merchants (100/s each) share the gateway load at 50/s each, so
// nothing is rate-limited and every request does the full path, including the Redis check.
//
//   docker compose … --profile bench run --rm k6 run --quiet -e TARGET=gateway -e LABEL=p3-gateway /scripts/p3-overhead.js
import http from 'k6/http';
import { Trend, Counter } from 'k6/metrics';
import { GATEWAYS, count, waitUntilReady } from './lib/flux.js';

const TARGET = __ENV.TARGET || 'gateway';
const RATE = Number(__ENV.RATE || 500);
const SECONDS = Number(__ENV.SECONDS || 60);
const MERCHANTS = 10;
const LABEL = __ENV.LABEL || `p3-${TARGET}`;
const GATEWAY = __ENV.GATEWAY_URL || 'http://gateway:8080';
const ADMIN_TOKEN = 'dev-admin-token-public-do-not-use-in-prod';   // Development's public dev token

const latency = new Trend('p3_latency', true);
const gwTime = new Trend('server_timing_gw', true);
const upstreamTime = new Trend('server_timing_upstream', true);
const notOk = new Counter('p3_not_ok');

export const options = {
  scenarios: {
    load: { executor: 'constant-arrival-rate', rate: RATE, timeUnit: '1s', duration: `${SECONDS}s`, preAllocatedVUs: 100, maxVUs: 400 },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  waitUntilReady(GATEWAYS);
  if (TARGET !== 'gateway') return { keys: [] };
  const keys = [];
  for (let i = 0; i < MERCHANTS; i++) {
    const response = http.post(`${GATEWAY}/admin/merchants`, JSON.stringify({ name: `p3-${Date.now()}-${i}`, plan: 'pro' }),
      { headers: { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN }, tags: { name: 'setup' } });
    if (response.status !== 201) throw new Error(`creating merchant failed: ${response.status} ${response.body}`);
    keys.push(response.json('apiKey'));
  }
  return { keys };
}

const serverTiming = /gw;dur=([\d.]+)(?:, upstream;dur=([\d.]+))?/;

export default function (data) {
  let response;
  if (TARGET === 'direct') {
    // Straight to the backend on the Compose network, with the header the gateway would add.
    response = http.get('http://payments:8080/payments/pay_1',
      { headers: { 'X-Flux-Merchant-Id': '00000000-0000-0000-0000-000000000001' }, tags: { name: 'direct' } });
  } else {
    const key = data.keys[(__VU + __ITER) % data.keys.length];
    response = http.get(`${GATEWAY}/v1/payments/pay_1`, { headers: { Authorization: `Bearer ${key}` }, tags: { name: 'gateway' } });
    const match = serverTiming.exec(response.headers['Server-Timing'] || '');
    if (match) {
      gwTime.add(Number(match[1]));
      if (match[2]) upstreamTime.add(Number(match[2]));
    }
  }
  latency.add(response.timings.duration);
  if (response.status !== 200) notOk.add(1, { status: String(response.status) });
}

function stats(data, name) {
  const v = data.metrics[name] && data.metrics[name].values;
  return v ? `p50 ${v['p(50)'].toFixed(3)} p90 ${v['p(90)'].toFixed(3)} p95 ${v['p(95)'].toFixed(3)} p99 ${v['p(99)'].toFixed(3)} max ${v.max.toFixed(3)} ms` : 'n/a';
}

export function handleSummary(data) {
  const lines = [
    `${LABEL}: ${RATE} req/s for ${SECONDS}s, target ${TARGET}`,
    `  client latency      ${stats(data, 'p3_latency')}`,
    `  Server-Timing gw    ${stats(data, 'server_timing_gw')}`,
    `  Server-Timing upstr ${stats(data, 'server_timing_upstream')}`,
    `  requests ${count(data, 'iterations')} | not-200 ${count(data, 'p3_not_ok')} | dropped ${count(data, 'dropped_iterations')}`,
    '',
  ];
  return { stdout: lines.join('\n'), [`/results/${LABEL}.json`]: JSON.stringify(data, null, 2) };
}
