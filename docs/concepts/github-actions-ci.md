# Continuous integration with GitHub Actions

> Field Guide: §22.1 CI, §22.7 Automated Build Pipelines, §22.11 Dependency Pinning, §22.24 Pipeline Least Privilege, §22.25 Ephemeral Build Environments · Introduced in: M1
> Code: [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml), [`global.json`](../../global.json), [`Directory.Packages.props`](../../Directory.Packages.props), [`Directory.Build.props`](../../Directory.Build.props)

## Problem
Code that passes on your laptop can still fail elsewhere: a forgotten file, a different SDK version, a test that only passes with your local state. If nothing checks every change automatically, `main` slowly stops working.

## Context
Every change to Flux, yours or mine, goes through a pull request. CI is the referee that decides whether a PR is mergeable. In M7 the same workflow will also *deploy*, which is the "CD" half.

## Solution
A **workflow** (YAML in `.github/workflows/`) runs **jobs** on fresh virtual machines (**runners**) when **events** happen (push, pull request). Each job is a list of **steps**: either shell commands (`run:`) or reusable **actions** (`uses:`).

```
push / PR ─▶ job "test":   checkout → setup .NET (from global.json) → restore → build (Release, warnings=errors) → test
                 │ needs
                 ▼
             job "images": checkout → buildx → build Gateway image ┐  (matrix: two parallel copies)
                                                 build Backend image ┘
```

### Why each line exists
| Line | Reason |
|------|--------|
| `on: push: branches: ['**']` + `pull_request` | Feedback on every branch push, plus a check on the PR merge result. |
| `concurrency … cancel-in-progress` | Pushing twice quickly cancels the stale run. Free minutes, faster feedback. |
| `permissions: contents: read` | The automatic `GITHUB_TOKEN` can only read. A malicious dependency running in CI can't push code or create releases. |
| `global-json-file: global.json` | CI installs **exactly** the SDK line the repo declares, the same one as your laptop. |
| `cache: true` | Reuses downloaded NuGet packages between runs, keyed on the `.csproj` files. |
| `TreatWarningsAsErrors` (in `Directory.Build.props`) | A warning that's ignored once gets ignored forever. Failing the build keeps the count at zero. |
| `needs: test` | Don't spend time building images for code that fails its tests. |
| `strategy.matrix` | One job definition, run once per service, in parallel. |
| `cache-from/to: type=gha` | Docker layer cache stored in GitHub's cache, so image builds are incremental too. |
| `timeout-minutes` | A hung test can't burn 6 hours of runner time. |

### Pinning, three ways
- **SDK** → `global.json` (`10.0.401`, may roll forward to a newer feature band).
- **NuGet packages** → `Directory.Packages.props` (central package management). Every project uses the same version, declared once.
- **Actions** → `@v4`-style major tags. A stricter, supply-chain-hardened option is pinning to a full commit SHA. That's a reasonable M7 upgrade, when the workflow gains deploy secrets.

### The test runner change in .NET 10
.NET 10's `dotnet test` can run on the new **Microsoft.Testing.Platform (MTP)** instead of the old VSTest. xUnit v3 test projects are *executables* (`<OutputType>Exe</OutputType>`) that include MTP directly. `global.json`'s `"test": { "runner": "Microsoft.Testing.Platform" }` opts the repo in, which is why the projects need no `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` packages.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| Self-hosted runner on Megatron | No SSH step for deploys, but it runs code from a **public** repo on your home server (ADR-0006). |
| GitLab CI, Jenkins, Azure Pipelines | Same concepts; Actions lives next to the code you already host on GitHub. |
| Git pre-commit hooks only | Easy to skip, and they run on your machine, not a clean one. |

## Trade-offs
- Minutes are free for public repos, but runs take 2–4 minutes, so CI is no replacement for running tests locally.
- Hosted runners are shared and noisy. Never benchmark on them; P1–P3 run on known hardware.

## Testing the workflow itself
Push the branch and watch the run: `gh run watch`. A deliberately failing test on a throwaway branch is a good way to see a red run once.

## Observability
The PR page shows each job's status. `gh run list` / `gh run view --log-failed` work from the terminal.

## Related
[docker-compose](docker-compose.md) · later: continuous-deployment (M7), secrets-management (M7)
