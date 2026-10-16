# Journal 07 — Showcase, instead of a deploy (M7)

- **Date:** 2026-10-16
- **Milestone:** M7 (re-scoped by [ADR-0011](../adr/0011-no-home-server-deploy-publish-images.md))
- **Branch:** `m7-showcase` (from `main`, after PRs #5–#8 were merged)

## What we did
1. **Asked whether to deploy at all, and decided not to.** Before writing any Kubernetes manifests, the owner asked whether deploying Flux to Megatron is worth it. The honest answer:
   - **Nobody else could see it:** tailnet-only means only the owner's devices.
   - **Public would be unwise:** a rate limiter with published dev keys, on a server shared with Slate.
   - **There's no frontend.**
   - **The repo's measured results are the real showcase.**

   Recorded as **[ADR-0011](../adr/0011-no-home-server-deploy-publish-images.md)**, superseding ADR-0006, with spec G8/N5 and §17 marked superseded, not deleted.
2. **Cleaned up GitHub:**
   - merged PRs #5 (M3) → #6 (M4) → #7 (M5) → #8 (M6) into `main` in order, all green;
   - deleted the merged branches, plus `m1-skeleton` and `m2-tenancy-auth` after confirming they held nothing `main` lacked.

   The repo now has one branch and no open PRs.
3. **CI publishes images:** on every push to `main`, the `images` job pushes `ghcr.io/kar6677thik/flux-gateway` and `flux-backend`, tagged `sha-<commit>` and `latest`. It's the only job with `packages: write`, and PRs still just build.
4. **[`docker-compose.ghcr.yml`](../../deploy/compose/docker-compose.ghcr.yml):** an override that runs the same system from the published images. Two commands, Docker only, no SDK.
5. **A recorded demo** ([`docs/images/demo.svg`](../images/demo.svg)), about 30 s:
   - an authenticated request with rate-limit headers;
   - a burst getting 2 allowed and 23 × `429`;
   - Redis stopped live: payments degraded, refunds `503`, fallback counters rising;
   - Redis back, normal again.

   Built from [`bench/demo.sh`](../../bench/demo.sh), recorded by [`bench/record_demo.py`](../../bench/record_demo.py) into an asciicast, rendered with svg-term. Every value is a real response.
6. **Charts** in the README, drawn by [`bench/make_charts.py`](../../bench/make_charts.py) from the raw k6 output:
   - E1 vs. P1 allowed counts;
   - E3 vs. P4 outcomes per second;
   - E3 vs. P4 p99 latency.

   Light and dark variants are served through `<picture>`, using a palette validated for colour-blind separation in both modes.
7. **README:** CI badge, the demo, the charts, a two-command "just Docker" run, and milestones complete.

## Why this order (why *now*)
- **Decide before building.** The deployment's cost (shared server, manual steps, upkeep) only pays off for a goal it serves. Asking "who will see this?" *before* writing manifests saved the work and still left the full plan (ADR-0006, spec §17) on record.
- **Clean up first.** Branching M7 from a `main` that already contains M3–M6 meant this PR's diff shows only M7, after M4's stacked-PR mishap.
- **Publishing comes after proving.** The images published are the ones whose behaviour the reports measured. Publishing before M6 would have shipped unmeasured claims.

## Concepts introduced
| Concept note | Field Guide |
|--------------|-------------|
| [Containers and registries (and continuous delivery)](../concepts/containers-and-registries.md) | §21, §22, §22.24, §25.27 |

New in the [glossary](../glossary.md): asciicast, compose override file, continuous delivery vs. deployment, immutable tag.

## Decisions made during M7
| Decision | Why |
|----------|-----|
| [ADR-0011](../adr/0011-no-home-server-deploy-publish-images.md): no home-server deploy; publish images plus a demo | Invisible if private, risky if public, no frontend. The measured results are the showcase. |
| GHCR, not Docker Hub | Authenticated by the repo's own `GITHUB_TOKEN`; no extra account or secret. |
| Tags `sha-<commit>` **and** `latest` | Immutable tags for reproducibility, a moving tag for "show me the current version". |
| Override file with `build: !reset null` | One source of truth for service settings; the override only swaps the image source and guarantees nothing builds locally. |
| Demo as an animated SVG from a real recording | It renders in the README with no player and no video hosting. Recording real output, not a mock-up, keeps the "measured only" rule. |
| A silent warm-up in `demo.sh` | The first recording showed `gw;dur=17 ms` from a cold gateway, against a measured steady state of ~0.3 ms (P3). Warm-up is the same methodology the reports use, and it's commented in the script. |
| The demo's 429 step uses refunds (free: 2/s) | With payments (10/s), starting 25 `curl` processes on Windows takes long enough to refill tokens. Refunds' small budget makes the limit visible reliably. |
| Charts as SVG with `<picture>` light/dark | Crisp at any size, and they follow the reader's GitHub theme. The dark variant is its own validated palette, not an inversion. |

## How to verify it yourself
**After this PR is merged**, CI on `main` publishes the images. Check the run, then the packages:
```bash
gh run list --branch main --limit 1
```
```bash
gh api /users/Kar6677thik/packages/container/flux-gateway/versions --jq '.[0].metadata.container.tags'
```
**One manual step, only you can do it:** make both packages public, once each. On GitHub go to your profile → **Packages** → `flux-gateway` → **Package settings** → **Change visibility** → **Public**, then the same for `flux-backend`. Until then, only you can pull them.

Then, from a fresh clone, Docker only:
```bash
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.ghcr.yml --profile single up -d
```
```bash
curl -i localhost:8080/v1/payments/pay_1 -H "Authorization: Bearer flux_devQuietShopimg7Ccuzt5fONTt77X0UcK82ZspFVHT"
```
Regenerate the visuals (optional):
```bash
python -I bench/make_charts.py
```
```bash
python -I bench/record_demo.py docs/images/demo.cast
```
```bash
npx --yes svg-term-cli --in docs/images/demo.cast --out docs/images/demo.svg --window --width 100 --height 34
```

## Exercises
1. Why does only the `images` job get `packages: write`, and why does it only push from `main`, never from pull requests?
2. A report says "measured with the image built from commit `abc123…`". Which tag lets someone run exactly that build a year later, and why not `latest`?
3. What's the difference between continuous *delivery* and continuous *deployment*? Which one does Flux do now?
4. `docker-compose.ghcr.yml` sets `build: !reset null`. What would happen without it, given that the base file builds `migrate` and `payments`?
5. The first demo recording showed `gw;dur=17.277`. Why was it right to add a warm-up before re-recording, and what would have been wrong about choosing a *different* recording with a better number?
6. ADR-0011 doesn't delete ADR-0006 or spec §17; it marks them superseded. Why keep a plan that was never built?

<details><summary>Answers</summary>

1. **Least privilege** (§22.24). Only the job that publishes needs to write packages; build and test never do. PRs can come from forks with arbitrary code, and if PR builds could push, anyone could overwrite `latest` with a malicious image. Pushing only from `main` means only reviewed, merged code becomes an artifact.
2. `sha-<full commit>`. It always refers to that one build. `latest` moves on every merge, so a year later it's a different image, and "the same command gives a different result" defeats reproducibility.
3. *Delivery:* every change on main yields a tested, releasable artifact. *Deployment:* each such artifact is also released automatically to a running environment. Flux now does **continuous delivery** (images in GHCR on every merge) and deliberately not deployment (ADR-0011).
4. Compose merges files. The base file's `build:` sections would remain, and `up` would build `migrate` and `payments` locally, needing the repo's Dockerfiles and a full .NET SDK build inside Docker, with the result tagged as the GHCR image name. The demo would no longer be "Docker only", and you couldn't be sure you were running the published artifact. `!reset null` removes the key outright.
5. A warm-up excludes a known, one-off cost (JIT, cold connections) that isn't what the demo shows. It's documented in the script, and it's the same rule the benchmarks follow. Re-recording until a lucky number appears would be *selecting* data: the number would no longer represent anything, and "every value is a real response" would be technically true but misleading.
6. It's design knowledge with its reasoning attached: the k3s layout, the migration Job, secrets handling, least-privilege sudo. If Kubernetes practice becomes the goal, the plan is ready. And the record of *why* it wasn't built is a decision someone may otherwise remake blindly. ADRs record decisions, including the decision not to do something.
</details>

## What could go wrong
- **`docker compose … -f docker-compose.ghcr.yml pull` → `denied`.** The packages are still private. Make them public (above). Until you do, only your own logged-in Docker can pull them (`gh auth token | docker login ghcr.io -u Kar6677thik --password-stdin`).
- **`manifest unknown` right after merging.** CI hasn't finished publishing yet. Wait for the `main` run to go green.
- **The demo fails at step 3.** `bench/demo.sh` stops the Compose Redis. If it's interrupted, run `docker compose -f deploy/compose/docker-compose.yml start redis`.
- **`make_charts.py` can't find files.** It reads raw k6 output from `bench/results/`, which is git-ignored and exists only where the experiments were run. Re-run E3 and P4 (journal 05) first.
- **The animated SVG is large (~1.2 MB).** It's fine on GitHub. Re-render with fewer recorded lines if it ever needs to be smaller.

## Where Flux stands
All seven milestones are done:
- the spec, the build in four milestones, seven experiments with measured reports, and eleven ADRs;
- 135 tests against real PostgreSQL and Redis;
- published images and a two-command demo.

The natural next projects are the ones this one deliberately left out (spec §3): Project 5's dashboards for `/metrics`, a sliding-window-counter algorithm, per-IP limits for unauthenticated traffic, and, if Kubernetes practice becomes the goal, ADR-0006's deployment, which is still on file.
