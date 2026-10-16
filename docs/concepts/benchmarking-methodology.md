# Benchmarking methodology

> Field Guide: §27 Performance engineering, §23.14 Load Testing, §1.11 Risk-driven development · Introduced in: M6 (practised since M4)
> Code: [`bench/`](../../bench/) · Reports: all of [`docs/reports/`](../reports/)

## Problem
Performance numbers are easy to produce and easy to get wrong. A benchmark can measure the load generator, the laptop's power settings, a cold JIT, or the test's own bugs, and print a confident number either way. "Report measured numbers only" (CLAUDE.md) is necessary, but not sufficient: the measurement has to measure the right thing.

## Context
Every claim in Flux's README traces back to a report in `docs/reports/`. This note collects the rules those reports follow, and the specific traps each experiment fell into or avoided.

## Solution: the rules, with the Flux experiment that taught each
| Rule | Why | Where it mattered |
|------|-----|-------------------|
| **Hypothesis first, with a number** | Turns "let's see" into a test that can fail | Every report starts with one (e.g. P1: within ±5% of 610) |
| **Open-loop load** (constant arrival rate) | A closed loop slows down with the server and hides latency | All rate experiments. E3 still dropped 240 iterations at 5 s waits, which was reported, not hidden |
| **Check the load generator kept up** (`dropped_iterations`) | Otherwise the run didn't apply the load it claims | 0 in E1, P1–P4. Not 0 in E3, and said so |
| **A baseline, or a before/after, on the same machine** | Absolute numbers on a virtualised laptop mean little; comparisons do | E1 vs. P1 (2.99× → 1.00×), P3 direct vs. gateway, P2 with vs. without the flood |
| **Repeat, and alternate conditions** | One run is an anecdote; alternating cancels drift (thermal, caches) | P2 ran baseline/flood ×3 interleaved; P3 direct/gateway ×2 |
| **Warm up, and say which runs were cold** | First runs pay JIT and cache-miss costs | E2's cold run (115 vs. 200); P2's first baseline (max 203 ms) |
| **Measure two independent ways** | If two methods agree, you're probably measuring the thing; if not, you learn why | P3: client-side difference vs. `Server-Timing`; E1/P1: k6's count vs. each gateway's `/metrics` |
| **Run a control when a result surprises you** | A surprising result is more often the setup than the system | P2's "faster under load" was traced to CPU power states with an unrelated CPU burner |
| **Record machine, versions, exact commands** | So someone else, or you in six months, can reproduce it | Every report's Machine and Exact commands sections |
| **Let the experiment find bugs** | That's the point of measuring | P4 found the cancellation `500`s; E3 found the replay burst; M3's `curl` showed a closed loop isn't a burst |
| **Separate measurement from interpretation** | Readers can disagree with the "why" and still trust the numbers | Reports mark explanations "our explanation, not measured directly" |

## Alternatives
- **Microbenchmarks** (BenchmarkDotNet) for one function in isolation, such as the token-bucket maths: precise, but they say nothing about the system. Flux's question was always "what does the *system* do?".
- **Production measurement** (real traffic, real hardware): the ultimate truth, available after M7. P1–P4 can optionally be rerun on Megatron (spec §22).

## Trade-offs
- **Laptop numbers include WSL2 virtualisation**, a shared CPU with k6, and Windows power management. That's fine for comparisons and wrong for capacity planning.
- **Rigour costs time.** P2 with its controls took eight runs. That's still cheaper than a wrong number on a résumé.

## Implementation
`bench/README.md` lists the rules and the switches. `run-outage.sh` and `analyze_outage.py` make the outage experiments one command each, so reruns are cheap.

## Testing
The scripts are validated by agreement between independent sources: k6 vs. `/metrics` (E1, P1), and client latency vs. `Server-Timing` (P3).

## Observability
The gateway's own histograms are the server-side half of every latency claim.

## Related
[load-testing-with-k6](load-testing-with-k6.md) · [coordinated-omission](coordinated-omission.md) · [latency-percentiles](latency-percentiles.md)
