# ============================================================
# OpenSandbox APP image  (fast build — uses pre-built base)
#
# Requires the base image to already exist in the registry.
# Only the .NET build + binary copy happens at image-build time,
# so incremental builds finish in seconds rather than minutes.
#
# Suggested tag format:
#   ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab:opensandbox-<YYYYMMDDHHMM>
#
# Build args:
#   BASE_IMAGE   — fully-qualified base image reference
#                  (default: ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab:opensandbox-base-latest)
#   CONFIGURATION — dotnet build configuration (default: Release)
#   OPENCLAW_ENABLE_OPENSANDBOX — feature flag forwarded to MSBuild (default: true)
#
# See scripts/build-opensandbox-app-image.ps1 for a ready-made
# build command.
# ============================================================

# Global ARGs (before any FROM) so they can be referenced in FROM instructions.
ARG BASE_IMAGE=ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab:opensandbox-base-latest

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG CONFIGURATION=Release
ARG OPENCLAW_ENABLE_OPENSANDBOX=true

WORKDIR /src

# Keep restore cache stable by copying project descriptors first.
COPY Directory.Build.props ./
COPY OpenClaw.Net.slnx ./
COPY src/OpenClaw.Core/OpenClaw.Core.csproj src/OpenClaw.Core/
COPY src/OpenClaw.Agent/OpenClaw.Agent.csproj src/OpenClaw.Agent/
COPY src/OpenClaw.Channels/OpenClaw.Channels.csproj src/OpenClaw.Channels/
COPY src/OpenClaw.PluginKit/OpenClaw.PluginKit.csproj src/OpenClaw.PluginKit/
COPY src/OpenClawNet.Sandbox.OpenSandbox/OpenClawNet.Sandbox.OpenSandbox.csproj src/OpenClawNet.Sandbox.OpenSandbox/
COPY src/OpenClaw.Gateway/OpenClaw.Gateway.csproj src/OpenClaw.Gateway/
COPY Kingcrab.ServiceDefaults/Kingcrab.ServiceDefaults.csproj Kingcrab.ServiceDefaults/

RUN dotnet restore src/OpenClaw.Gateway/OpenClaw.Gateway.csproj

COPY src/ src/
COPY Kingcrab.ServiceDefaults/ Kingcrab.ServiceDefaults/

RUN dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj \
    -c ${CONFIGURATION} \
    -o /out/publish \
    -p:OpenClawEnableOpenSandbox=${OPENCLAW_ENABLE_OPENSANDBOX} \
    --no-restore

# ---------------------------------------------------------------------------
# Runtime — swap in the pre-built base so no package installation is needed.
# ---------------------------------------------------------------------------
FROM ${BASE_IMAGE} AS runtime

COPY --from=build --chown=10001:10001 /out/publish/ /app/

ENV ASPNETCORE_URLS=http://0.0.0.0:18789 \
    DOTNET_EnableDiagnostics=1 \
    HOME=/home/openclaw \
    OpenClaw__BindAddress=0.0.0.0 \
    OpenClaw__Port=18789 \
    OpenClaw__Runtime__Mode=jit \
    OpenClaw__Memory__StoragePath=/app/memory \
    OpenClaw__Memory__Sqlite__DbPath=/app/memory/openclaw.db \
    OpenClaw__Tooling__WorkspaceRoot=/workspace \
    OpenClaw__Tooling__AllowShell=true \
    OpenClaw__Tooling__AllowedReadRoots__0=/workspace \
    OpenClaw__Tooling__AllowedWriteRoots__0=/workspace \
    OpenClaw__Plugins__Enabled=true \
    OpenClaw__Security__TrustForwardedHeaders=true

EXPOSE 18789

USER root

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
    CMD ["/app/OpenClaw.Gateway", "--health-check"]

ENTRYPOINT ["/app/OpenClaw.Gateway"]
