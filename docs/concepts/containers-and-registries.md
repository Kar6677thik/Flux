# Containers and registries (and continuous delivery)

> Field Guide: §21 Containers, §22 CI/CD (artifact management, immutable builds), §22.24 least privilege in pipelines, §25.27 Supply-chain security · Introduced in: M7
> Code: [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) (`images` job), [`deploy/compose/docker-compose.ghcr.yml`](../../deploy/compose/docker-compose.ghcr.yml) · Decision: [ADR-0011](../adr/0011-no-home-server-deploy-publish-images.md)

## Problem
Until M7, running Flux meant cloning the repo, installing the .NET 10 SDK and building two images locally. Every machine produced its *own* images from the same source, so "it works for me" couldn't be checked byte for byte. A reviewer who just wants to *see* it run hits the biggest friction before anything happens.

## Context
A **container registry** stores built images so they can be pulled anywhere: Docker Hub, GHCR, ECR, ACR. CI builds an image once, pushes it, and everything downstream (a laptop, a test environment, production) runs *that exact artifact*. This is the core of **continuous delivery**.

## Solution
```
push to main ──▶ CI: build + test ──▶ build images ──push──▶ ghcr.io/kar6677thik/flux-gateway:sha-<commit>
                                                                                          :latest
pull request ──▶ CI: build + test ──▶ build images (not pushed)

anyone: docker compose -f docker-compose.yml -f docker-compose.ghcr.yml --profile single up -d
        └─ same services and settings as the local build; only the image source differs
```

### Tags: immutable vs. moving
| Tag | Points to | Use it for |
|-----|-----------|-----------|
| `sha-<full commit>` | exactly one build, forever | reproducing a report, rollbacks, anything you'll want to explain later |
| `latest` | whatever main built most recently | "just show me the current version" demos |

A deployment (had there been one) would pin the SHA tag. `latest` silently changes under you, so "it worked yesterday" stops being checkable.

### Continuous delivery vs. continuous deployment
- **Delivery:** every change on main produces a *releasable* artifact. Flux does this: tested images in GHCR, on every merge.
- **Deployment:** every such change is also *released* automatically. That was ADR-0006's plan, superseded by ADR-0011. The artifacts are ready; the decision not to release them anywhere is deliberate.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| Docker Hub | The most widely known registry, but a separate account and token, plus anonymous pull rate limits. GHCR uses the repo's own `GITHUB_TOKEN` and sits next to the code. |
| Build locally only (pre-M7) | No registry needed, but every user needs the SDK and gets their own build |
| Release binaries (`dotnet publish` zips) | Need a runtime installed, plus PostgreSQL and Redis set up by hand. Compose plus images packages all of it. |

## Trade-offs
- **Supply-chain trust:** consumers trust whatever CI pushed. Mitigations:
  - **least privilege:** only the `images` job can write packages, and it only pushes from `main`, never from PRs, which could come from forks;
  - **labels** linking each image to its source repo and commit (`org.opencontainers.image.source` / `revision`).

  Signing (cosign) and SBOMs are the next steps (§25.27).
- **Visibility:** GHCR packages start **private** on first publish, so the owner must flip them to public once (ADR-0011). Until then, `docker compose … pull` fails for everyone else with "denied".
- **Storage:** every push to main keeps two more images. GHCR's retention policies can prune old `sha-*` tags later.

## Implementation
- `${GITHUB_REPOSITORY_OWNER,,}`: registry paths must be lowercase, but the account is `Kar6677thik`. Bash's `,,` lowercases it.
- `push: ${{ env.PUBLISH == 'true' }}` keeps one build step for both cases: PRs prove the Dockerfile builds; main also publishes.
- `docker-compose.ghcr.yml` uses `build: !reset null` (Compose 2.24+) to *remove* the base file's `build:` sections, so nothing builds locally even by accident.

## Testing
- Every PR's CI builds both images (unchanged since M1).
- After the M7 merge, the first `main` run must publish, and `docker compose … -f docker-compose.ghcr.yml pull` must succeed. Journal 07 lists the commands.

## Observability
The repo's *Packages* sidebar lists each image with its tags and the commit that built it.

## Related
[docker-compose](docker-compose.md) · [github-actions-ci](github-actions-ci.md) · ADR-0006 (the deployment that wasn't), ADR-0011
