# syntax=docker/dockerfile:1
# Build context is the repository root (it needs global.json and the Directory.*.props files).

# ---- build stage: full SDK, thrown away afterwards -------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# Restore from the project file alone first: this layer is cached until dependencies change,
# so editing a .cs file doesn't re-download packages.
COPY src/Flux.Backend/Flux.Backend.csproj src/Flux.Backend/
RUN dotnet restore src/Flux.Backend/Flux.Backend.csproj
COPY src/Flux.Backend/ src/Flux.Backend/
RUN dotnet publish src/Flux.Backend/Flux.Backend.csproj -c Release -o /app --no-restore

# ---- runtime stage: ASP.NET runtime only, no compiler or SDK -----------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The base image ships a non-root "app" user; never run the service as root.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Flux.Backend.dll"]
