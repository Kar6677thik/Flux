# Containers, multi-stage Dockerfiles and Docker Compose

> Field Guide: §21.1 Containerization, §21.2 Multi-Stage Builds, §25.28 Container Hardening, §15.18 Network Policies · Introduced in: M1
> Code: [`src/Flux.Gateway/Dockerfile`](../../src/Flux.Gateway/Dockerfile), [`src/Flux.Backend/Dockerfile`](../../src/Flux.Backend/Dockerfile), [`deploy/compose/docker-compose.yml`](../../deploy/compose/docker-compose.yml), [`.dockerignore`](../../.dockerignore)

## Problem
"It works on my machine": different .NET versions, missing environment variables, a port already in use. Running 5+ processes by hand (2 backends, gateways, later PostgreSQL, Redis and nginx) is error-prone, and the server doesn't even have .NET installed.

## Context
Flux runs the same artefact in three places: your laptop (Compose), CI (GitHub runner), and Megatron (k3s). A container image is that artefact.

## Solution
- **Image**: a filesystem snapshot plus a start command. It is immutable and versioned.
- **Container**: a running instance of an image, isolated with Linux namespaces and cgroups.
- **Compose**: a YAML description of several containers, their configuration and their private network, started with one command.

### Multi-stage Dockerfile
```
FROM sdk:10.0   AS build    ← ~900 MB: compiler, NuGet, everything
  restore (cached layer) → copy source → publish to /app
FROM aspnet:10.0 AS runtime ← ~220 MB: just the runtime
  COPY --from=build /app .   ← only the compiled output crosses over
```
The final image contains no SDK, no source and no build tools. It's smaller to pull, and there's less for an attacker to use.

### Layer caching
Docker caches each instruction's result and reuses it until an input changes. We copy **only the `.csproj`** and run `restore` *before* copying the source. So when you edit `Program.cs`, the slow package download stays cached; only `COPY src` and `publish` rerun.

### Hardening details
- `USER $APP_UID`: the official .NET images include a non-root `app` user. If the gateway is ever compromised, the attacker isn't root inside the container.
- `.dockerignore` keeps `bin/`, `obj/`, `.git/` and `docs/` out of the build context. That makes builds faster and stops stale local build output leaking into the image.
- The build context is the **repo root**, because the build needs `global.json` and the `Directory.*.props` files.

### Compose networking
Compose creates a private network (`flux_default`) in which each service's **name is its DNS name**. The gateway forwards to `http://payments:8080`. Only the gateway publishes a port (`8080:8080`), so from your laptop the backends simply don't exist. That's the local equivalent of the k3s NetworkPolicy planned in spec §17.

### Profiles
`profiles: [single]` means "only start this when that profile is asked for". Backends have no profile, so they always start. Later, `cluster` adds 3 gateways + nginx (M4) and `bench` adds k6.

## Alternatives
| Option | Trade-off |
|--------|-----------|
| Run with `dotnet run` | Fastest inner loop for one service; painful for 6. We support both (launch profiles). |
| `dotnet publish /t:PublishContainer` (SDK-built images, no Dockerfile) | Neat, but hides the layers you're supposed to learn about here. |
| Alpine / chiseled images | Smaller still. Chiseled Ubuntu images are a good later hardening step; they have no shell, which makes debugging harder. |
| Kubernetes locally (kind, k3d) | Matches the server more closely, but heavier for daily work. Compose for dev, k3s for the server. |

## Trade-offs
- On Windows, Docker Desktop runs a Linux VM (WSL2), which adds virtualisation overhead to every benchmark (spec §15).
- Image tags like `aspnet:10.0` move as patches ship. That's good for security fixes, but it means rebuilding the same commit later can give a slightly different image. M7 pins images by **git SHA** for deployments.

## Try it
Docker Desktop has to be running for these commands:
```bash
docker compose -f deploy/compose/docker-compose.yml --profile single up --build -d
curl -i -X POST localhost:8080/v1/payments -H "Content-Type: application/json" -d '{"amount":50000}'
docker compose -f deploy/compose/docker-compose.yml --profile single logs gateway
docker compose -f deploy/compose/docker-compose.yml --profile single down
```

## Testing
CI's `images` job builds both Dockerfiles on every push. If a Dockerfile breaks, CI goes red.

## Observability
`docker compose logs -f gateway` shows JSON log lines (Production environment) including `requestId`.

## Related
[reverse-proxy](reverse-proxy.md) · [github-actions-ci](github-actions-ci.md) · later: containers-and-registries (M7)
