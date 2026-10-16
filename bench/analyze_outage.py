"""Turns a redis-outage.js run into the tables used in the E3 and P4 reports.

    python -I bench/analyze_outage.py bench/results/<label>.ndjson bench/results/<label>-events.txt

Every request is placed in a phase by the moment it was *sent* (k6 records the moment it finished,
so we subtract its duration): before the break, during the outage, or after Redis came back.
Standard library only, so it runs anywhere Python 3.11+ does.
"""

import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime


def parse_time(text):
    # k6 writes nanoseconds; datetime understands microseconds.
    text = re.sub(r"(\.\d{6})\d+", r"\1", text).replace("Z", "+00:00")
    return datetime.fromisoformat(text).timestamp() * 1000


def percentile(sorted_values, p):
    if not sorted_values:
        return float("nan")
    index = min(len(sorted_values) - 1, max(0, round(p / 100 * len(sorted_values) + 0.5) - 1))
    return sorted_values[index]


def main(ndjson_path, events_path):
    events = {}
    for line in open(events_path, encoding="utf-8"):
        name, epoch_ms, mode = line.split()
        events[name] = int(epoch_ms)
    broke, healed = events["break"], events["heal"]

    samples = []
    for line in open(ndjson_path, encoding="utf-8"):
        point = json.loads(line)
        if point.get("type") != "Point" or point.get("metric") != "flux_req":
            continue
        data = point["data"]
        sent = parse_time(data["time"]) - data["value"]
        samples.append((sent, data["value"], data["tags"]))
    if not samples:
        sys.exit("no flux_req samples found")

    def phase(sent):
        return "before" if sent < broke else "during" if sent < healed else "after"

    groups = defaultdict(list)
    for sent, ms, tags in samples:
        groups[(phase(sent), tags["route"])].append((ms, tags))

    print(f"outage ({mode}): {(healed - broke) / 1000:.1f} s; {len(samples)} requests\n")
    print("| Phase | Route | Requests | Statuses | Degraded | p50 ms | p99 ms | max ms |")
    print("|-------|-------|----------|----------|----------|--------|--------|--------|")
    for ph in ("before", "during", "after"):
        for route in ("payments", "refunds"):
            rows = groups.get((ph, route), [])
            if not rows:
                continue
            latencies = sorted(ms for ms, _ in rows)
            statuses = Counter(tags["status"] for _, tags in rows)
            degraded = sum(1 for _, tags in rows if tags["degraded"] == "true")
            status_text = "; ".join(f"{s}x{n}" for s, n in sorted(statuses.items()))
            print(f"| {ph} | {route} | {len(rows)} | {status_text} | {degraded} | "
                  f"{percentile(latencies, 50):.2f} | {percentile(latencies, 99):.2f} | {latencies[-1]:.2f} |")

    # Recovery: the first request sent after Redis came back that was served normally again.
    def first_normal(route):
        times = [sent for sent, _, tags in samples
                 if sent >= healed and tags["route"] == route and tags["status"] == "200" and tags["degraded"] == "false"]
        return (min(times) - healed) / 1000 if times else None

    def first_failure(route, predicate):
        times = [sent for sent, _, tags in samples if sent >= broke and tags["route"] == route and predicate(tags)]
        return (min(times) - broke) / 1000 if times else None

    print()
    for route in ("payments", "refunds"):
        hit = first_failure(route, lambda t: t["status"] != "200" or t["degraded"] == "true")
        back = first_normal(route)
        print(f"{route}: first affected request {hit if hit is None else f'{hit:.2f} s'} after the break; "
              f"normal again {back if back is None else f'{back:.2f} s'} after Redis returned")

    print("\nper-second timeline (seconds from the break; statuses, degraded, p99 ms)")
    timeline = defaultdict(list)
    for sent, ms, tags in samples:
        timeline[(int((sent - broke) // 1000), tags["route"])].append((ms, tags))
    for second in range(-3, int((healed - broke) / 1000) + 8):
        cells = []
        for route in ("payments", "refunds"):
            rows = timeline.get((second, route), [])
            if not rows:
                cells.append(f"{route}: -")
                continue
            statuses = Counter(tags["status"] for _, tags in rows)
            degraded = sum(1 for _, tags in rows if tags["degraded"] == "true")
            p99 = percentile(sorted(ms for ms, _ in rows), 99)
            cells.append(f"{route}: {dict(sorted(statuses.items()))} deg={degraded} p99={p99:.1f}")
        print(f"  t{second:+3d}s  " + " | ".join(cells))


if __name__ == "__main__":
    mode = open(sys.argv[2], encoding="utf-8").read().split()[2]
    main(sys.argv[1], sys.argv[2])
