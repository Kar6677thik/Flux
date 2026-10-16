// Shared helpers for the Flux experiments. Runs inside the k6 container on the Compose network,
// so gateways are reachable by service name (gateway, gateway-1, nginx …).
import http from 'k6/http';
import { sleep } from 'k6';

// The public dev keys seeded in Development (src/Flux.Gateway/Data/DevSeed.cs). Not secrets.
export const KEYS = {
  noisy: 'flux_devNoisyCorp5crOvj96ohLOnSMDJFRWBATTMjtyTLM',   // plan free: payments 10/s, burst 10
  quiet: 'flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT',   // plan pro: payments 100/s, burst 100
};

export const BASE_URL = __ENV.BASE_URL || 'http://nginx:8080';

// Gateways to inspect directly (readiness, /metrics), bypassing the load balancer.
export const GATEWAYS = (__ENV.GATEWAYS || 'gateway-1,gateway-2,gateway-3').split(',').map((g) => g.trim());

export function auth(key) {
  return { headers: { Authorization: `Bearer ${key}` } };
}

// A freshly (re)started gateway is unready until its plan catalog loads; don't measure before that.
export function waitUntilReady(gateways, timeoutSeconds = 60) {
  for (const gateway of gateways) {
    const deadline = Date.now() + timeoutSeconds * 1000;
    while (http.get(`http://${gateway}:8080/readyz`, { tags: { name: 'readyz' } }).status !== 200) {
      if (Date.now() > deadline) throw new Error(`${gateway} not ready after ${timeoutSeconds}s`);
      sleep(0.5);
    }
  }
}

// Reads one gateway's flux_requests_total for a route group, straight from its /metrics.
export function requestCounts(gateway, routeGroup) {
  const text = http.get(`http://${gateway}:8080/metrics`, { tags: { name: 'metrics' } }).body;
  const counts = {};
  for (const line of text.split('\n')) {
    if (!line.startsWith('flux_requests_total{') || !line.includes(`route_group="${routeGroup}"`)) continue;
    const outcome = /outcome="([a-z_]+)"/.exec(line)[1];
    counts[outcome] = Number(line.trim().split(' ').pop());
  }
  return counts;
}

export function count(data, metric) {
  return data.metrics[metric] ? data.metrics[metric].values.count : 0;
}
