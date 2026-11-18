# ADR-0006: Deploy to k3s on the home server via GitHub Actions, Tailscale and a narrow sudo helper; tailnet only

- **Status:** Superseded by [ADR-0011](0011-no-home-server-deploy-publish-images.md) (2026-10-16): Flux isn't deployed to the home server. This design was never built and is kept as a worked plan.
- **Redacted 2026-11-18:** identifiers of the private setup (addresses, hostnames, paths, usernames, versions) were replaced with generic descriptions. The reasoning is unchanged.
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M7)

## Context
The requirement is that deployment happens via GitHub Actions, never by hand on the server.

The home server, reached over the tailnet (as of 2026-10-15):
- a single-node k3s server: 8 cores, 8 GB RAM, shared with another project
- disk nearly full at the time
- **no Docker**; no nginx
- Tailscale Serve already publishes the other project on the tailnet

The deploy user is a dedicated account without passwordless sudo, and it cannot read the k3s kubeconfig.

The other project already deploys successfully with this pattern: GitHub-hosted runner → `tailscale/github-action` (joining the tailnet with a CI tag) → `scp` a manifest bundle → `ssh` → `sudo -n` on a root-owned deploy helper.

## Decision
- **Runtime:** a new k3s namespace `flux`.
- **Images:** pushed to GHCR, public, tagged with the git SHA.
- **CI/CD:** the same pattern as the other project. The deploy job runs only on `main` and only after tests pass.
- **Access:** a new, Flux-only SSH deploy key, and a Flux-only, root-owned deploy helper, callable through one `sudoers` rule. The helper only applies manifests to namespace `flux` and waits for the rollout.
- **Exposure:** **tailnet only**. Tailscale Serve publishes the API on the tailnet, on its own port, separate from the other project's mapping, forwarding to the gateway's NodePort.
- **Postgres storage:** the k3s built-in `local-path` StorageClass, so no `sudo mkdir` is needed.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Install Docker + Compose on the home server | Matches local dev exactly, but means a new daemon, a sudo install, and duplicated container images on a nearly full disk. k3s is already running. |
| Self-hosted GitHub Actions runner on the home server | No SSH or Tailscale step needed, but a runner on a home server executes workflow code from a **public** repo. That risk is not worth taking. |
| Give CI a kubeconfig for the cluster | Broad cluster-admin access from CI. The helper limits CI to one namespace and one action (least privilege, Field Guide §22.24). |
| Reuse the other project's deploy key and helper | Coupling: a Flux compromise would also reach that project. Separate keys and helpers keep the blast radius small. |
| Public via Tailscale Funnel | Real internet abuse traffic and more hardening work. Not needed for learning. |

## Consequences
- You must do a few one-time steps that need sudo or account access (spec §17).
- A k8s Service balances connections at L4, not requests at L7, so the 3× experiment is run locally behind nginx instead (spec §21).
- Disk pressure on the home server is a real risk to both Flux and the other project, and must be addressed before M7.

## Revisit when
The home server gets a container registry, or more projects need the same deploy pattern. At that point, consider a GitOps controller such as Flux CD or Argo CD. Note the naming clash: Flux CD is a different project from ours.
