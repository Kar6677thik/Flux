"""Draws the README charts from the experiments' raw k6 output, in light and dark variants.

    python -I bench/make_charts.py            # needs matplotlib; reads bench/results/, writes docs/images/

Numbers for the limits chart come from the E1/P1 reports. The outage charts are computed from the
per-request NDJSON of E3 (e3-stop) and P4 (p4-stop-fixed), exactly as analyze_outage.py does.
Colours are the dataviz reference palette, validated (light and dark) for adjacent use.
"""

import json
import re
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
RESULTS = ROOT / "bench" / "results"
OUT = ROOT / "docs" / "images"

THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781", grid="#e1e0d9",
                  axis="#c3c2b7", band="#f0efec",
                  series=["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4"]),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781", grid="#2c2c2a",
                 axis="#383835", band="#2c2c2a",
                 series=["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181"]),
}

# Outcome → categorical slot, in fixed order (stacked bottom→top in the same order, so every
# touching pair is an adjacent, validated pair).
OUTCOMES = ["200", "200 degraded", "429", "500", "503"]


def style(ax, t):
    ax.set_facecolor(t["surface"])
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(t["axis"])
    ax.tick_params(colors=t["muted"], labelsize=9, length=0)
    ax.grid(axis="y", color=t["grid"], linewidth=0.8)
    ax.set_axisbelow(True)


def figure(t, *args, **kwargs):
    fig, axes = plt.subplots(*args, **kwargs)
    fig.patch.set_facecolor(t["surface"])
    return fig, axes


def save(fig, name, mode):
    OUT.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUT / f"{name}-{mode}.svg", facecolor=fig.get_facecolor(), bbox_inches="tight", pad_inches=0.25)
    plt.close(fig)


def limits_chart(mode):
    t = THEMES[mode]
    rows = [  # (label, mean, run range label) from reports E1 and P1
        ("3 gateways, in-memory buckets (E1)", 1824.0, "1,822–1,825"),
        ("3 gateways, shared Redis bucket (P1)", 608.7, "608–609"),
        ("1 gateway, in-memory (E1 baseline)", 609.0, "609"),
    ]
    fig, ax = figure(t, figsize=(8, 2.9))
    style(ax, t)
    ax.grid(axis="y", visible=False)
    ax.grid(axis="x", color=t["grid"], linewidth=0.8)
    height = 0.42
    for i, (label, value, runs) in enumerate(rows):
        y = len(rows) - 1 - i
        ax.barh(y, value, height=height, color=t["series"][0], linewidth=0, zorder=2)
        ax.text(value + 25, y, runs, va="center", ha="left", fontsize=10, color=t["ink"])
        ax.text(-20, y, label, va="center", ha="right", fontsize=10, color=t["ink2"])
    ax.axvline(610, color=t["ink2"], linewidth=1.2, linestyle=(0, (4, 3)))
    ax.text(630, len(rows) - 0.42, "plan limit  B + r·T = 610", ha="left", va="bottom", fontsize=9, color=t["ink2"])
    ax.set_xlim(0, 2050)
    ax.set_ylim(-0.6, len(rows) - 0.2)
    ax.set_yticks([])
    ax.set_xlabel("requests allowed in 60 s (noisy-corp offering 100 req/s; free plan 10/s)", fontsize=9, color=t["muted"])
    ax.set_title("Per-instance buckets let a merchant through at 3× its limit; one shared bucket fixes it",
                 loc="left", fontsize=11.5, color=t["ink"], pad=14)
    save(fig, "limits", mode)


def parse_time(text):
    text = re.sub(r"(\.\d{6})\d+", r"\1", text).replace("Z", "+00:00")
    return datetime.fromisoformat(text).timestamp() * 1000


def load_outage(label):
    events = {}
    for line in open(RESULTS / f"{label}-events.txt", encoding="utf-8"):
        name, epoch_ms, _ = line.split()
        events[name] = int(epoch_ms)
    samples = []
    for line in open(RESULTS / f"{label}.ndjson", encoding="utf-8"):
        point = json.loads(line)
        if point.get("type") == "Point" and point.get("metric") == "flux_req":
            data = point["data"]
            sent = parse_time(data["time"]) - data["value"]
            samples.append(((sent - events["break"]) / 1000, data["value"], data["tags"]))
    return samples, (events["heal"] - events["break"]) / 1000


def outcome(tags):
    return "200 degraded" if tags["status"] == "200" and tags["degraded"] == "true" else tags["status"]


SECONDS = range(-5, 31)


def outage_chart(mode, runs):
    t = THEMES[mode]
    fig, axes = figure(t, 2, 2, figsize=(10, 5.6), sharex=True, gridspec_kw=dict(hspace=0.45, wspace=0.18))
    for row, (label, title, samples, outage) in enumerate(runs):
        for col, route in enumerate(("payments", "refunds")):
            ax = axes[row][col]
            style(ax, t)
            counts = defaultdict(Counter)
            for second, _, tags in samples:
                if tags["route"] == route:
                    counts[int(second // 1)][outcome(tags)] += 1
            ax.axvspan(0, outage, color=t["band"], zorder=0, linewidth=0)
            bottoms = [0] * len(SECONDS)
            for slot, name in enumerate(OUTCOMES):
                values = [counts[s][name] for s in SECONDS]
                if any(values):
                    # A 1.5px surface-coloured edge is the gap between stacked segments.
                    ax.bar(list(SECONDS), values, bottom=bottoms, width=0.86, color=t["series"][slot],
                           edgecolor=t["surface"], linewidth=1.5, label=name, zorder=2)
                    bottoms = [b + v for b, v in zip(bottoms, values)]
            ax.set_title(f"{title} · {route}", loc="left", fontsize=10, color=t["ink"])
            ax.text(outage / 2, max(bottoms) * 1.04, "Redis down", ha="center", va="bottom", fontsize=8.5, color=t["ink2"])
            ax.set_ylim(0, max(bottoms) * 1.22)
            if col == 0:
                ax.set_ylabel("requests / s", fontsize=9, color=t["muted"])
    for ax in axes[1]:
        ax.set_xlabel("seconds from the moment Redis was stopped", fontsize=9, color=t["muted"])
    handles = {}
    for ax in axes.flat:
        for handle, name in zip(*ax.get_legend_handles_labels()):
            handles.setdefault(name, handle)
    ordered = [n for n in OUTCOMES if n in handles]
    fig.legend([handles[n] for n in ordered], ordered, loc="upper center", ncol=len(ordered), frameon=False,
               fontsize=9, labelcolor=t["ink2"], bbox_to_anchor=(0.5, 1.0), handlelength=1.2)
    fig.suptitle("A 21 s Redis outage: client defaults fail everything (E3); the resilient limiter keeps payments up (P4)",
                 x=0.06, ha="left", y=1.06, fontsize=11.5, color=t["ink"])
    save(fig, "outage", mode)


def latency_chart(mode, runs):
    t = THEMES[mode]
    fig, ax = figure(t, figsize=(8.5, 3.6))
    style(ax, t)
    outage = runs[0][3]
    ax.axvspan(0, outage, color=t["band"], zorder=0, linewidth=0)
    colours = {"E3": t["series"][1], "P4": t["series"][0]}
    for label, title, samples, _ in runs:
        per_second = defaultdict(list)
        for second, ms, tags in samples:
            if tags["route"] == "payments":
                per_second[int(second // 1)].append(ms)
        xs = [s for s in SECONDS if per_second[s]]
        ys = []
        for s in xs:
            v = sorted(per_second[s])
            ys.append(v[min(len(v) - 1, int(0.99 * len(v)))])
        key = label.split("-")[0].upper()
        ax.plot(xs, ys, color=colours[key], linewidth=2, label=title, zorder=3, solid_capstyle="round")
        ax.text(xs[-1] + 0.6, ys[-1], key, va="center", fontsize=9, color=t["ink2"])
    ax.set_yscale("log")
    ax.set_yticks([5, 10, 50, 100, 500, 1000, 5000])
    ax.set_yticklabels(["5 ms", "10 ms", "50 ms", "100 ms", "500 ms", "1 s", "5 s"])
    ax.text(outage / 2, 9000, "Redis down", ha="center", va="bottom", fontsize=8.5, color=t["ink2"])
    ax.set_ylim(4, 15000)
    ax.set_xlim(-5, 32)
    ax.set_xlabel("seconds from the moment Redis was stopped", fontsize=9, color=t["muted"])
    ax.legend(frameon=False, fontsize=9, labelcolor=t["ink2"], loc="upper right", bbox_to_anchor=(1.0, 1.18), ncol=2)
    ax.set_title("Payments p99 latency per second, log scale", loc="left", fontsize=11.5, color=t["ink"], pad=24)
    save(fig, "outage-latency", mode)


def main():
    plt.rcParams.update({"font.family": ["Segoe UI", "DejaVu Sans"], "svg.fonttype": "path"})
    e3, e3_outage = load_outage("e3-stop")
    p4, p4_outage = load_outage("p4-stop-fixed")
    runs = [("e3-stop", "E3: client defaults", e3, e3_outage), ("p4-stop-fixed", "P4: resilient limiter", p4, p4_outage)]
    for mode in THEMES:
        limits_chart(mode)
        outage_chart(mode, runs)
        latency_chart(mode, runs)
    print("wrote", sorted(p.name for p in OUT.glob("*.svg")))


if __name__ == "__main__":
    main()
