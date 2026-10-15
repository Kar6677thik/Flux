# ADR-0006: Deploy to k3s on Megatron via GitHub Actions, Tailscale and a narrow sudo helper; tailnet only

- **Status:** Accepted
- **Date:** 2026-10-15
- **Milestone:** M0 (implemented in M7)

## Context
The requirement is that deployment happens via GitHub Actions, never by hand on the server.

The home server, Megatron (as of 2026-10-15):
- Linux Mint 22.1, 8 cores, 7.6 GiB RAM (about 4.8 GiB available)
- 21 GB of disk free (83% used)
- **k3s v1.36** running; **no Docker**; no nginx
- Tailscale Serve already maps `https://megatron.tailf67d61.ts.net/` (port 443) to Slate

The deploy user `kar_thik` has no passwordless sudo and cannot read the k3s kubeconfig.

Slate already deploys successfully with this pattern: GitHub-hosted runner → `tailscale/github-action` (`tag:ci`) → `scp` a manifest bundle → `ssh … sudo -n /usr/local/sbin/slate-deploy <dir>`.

## Decision
- **Runtime:** a new k3s namespace `flux`.
- **Images:** pushed to GHCR, public, tagged with the git SHA.
- **CI/CD:** the same pattern as Slate. The deploy job runs only on `main` and only after tests pass.
- **Access:** a new, Flux-only SSH deploy key, and a Flux-only sudo helper, `/usr/local/sbin/flux-deploy`. The helper only applies manifests to namespace `flux` and waits for the rollout.
- **Exposure:** **tailnet only**. Tailscale Serve HTTPS on its own port (planned `:8443`) → gateway NodePort.
- **Postgres storage:** the k3s built-in `local-path` StorageClass, so no `sudo mkdir` is needed.

## Alternatives considered
| Option | Why not |
|--------|---------|
| Install Docker + Compose on Megatron | Matches local dev exactly, but means a new daemon, a sudo install, and duplicated container images on a nearly full disk. k3s is already running. |
| Self-hosted GitHub Actions runner on Megatron | No SSH or Tailscale step needed, but a runner on a home server executes workflow code from a **public** repo. That risk is not worth taking. |
| Give CI a kubeconfig for the cluster | Broad cluster-admin access from CI. The helper limits CI to one namespace and one action (least privilege, Field Guide §22.24). |
| Reuse Slate's deploy key and helper | Coupling: a Flux compromise would also reach Slate. Separate keys and helpers keep the blast radius small. |
| Public via Tailscale Funnel | Real internet abuse traffic and more hardening work. Not needed for learning. |

## Consequences
- You must do a few one-time steps that need sudo or account access (spec §17).
- A k8s Service balances connections at L4, not requests at L7, so the 3× experiment is run locally behind nginx instead (spec §21).
- Disk pressure on Megatron is a real risk to both Flux and Slate, and must be addressed before M7.

## Revisit when
The home server gets a container registry, or more projects need the same deploy pattern. At that point, consider a GitOps controller such as Flux CD or Argo CD. Note the naming clash: Flux CD is a different project from ours.
