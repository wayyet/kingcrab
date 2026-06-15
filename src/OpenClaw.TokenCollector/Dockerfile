# Out-of-sandbox token usage collector.
# Plain JIT (no AOT/chiseled): Confluent.Kafka needs the native librdkafka.
# Build context = repo root (collector references ../OpenClaw.Core), e.g.:
#   docker build -f src/OpenClaw.TokenCollector/Dockerfile -t openclaw.net-token-collector:dev .

# ── Stage 1: Build + Publish ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy build props for layer caching (Directory.Build.props sits 2 levels above the csproj).
COPY Directory.Build.props ./

# Copy csproj files individually for restore caching.
COPY src/OpenClaw.Core/OpenClaw.Core.csproj                 src/OpenClaw.Core/
COPY src/OpenClaw.TokenCollector/OpenClaw.TokenCollector.csproj src/OpenClaw.TokenCollector/

RUN dotnet restore src/OpenClaw.TokenCollector/OpenClaw.TokenCollector.csproj

# Copy sources needed by the collector.
COPY src/OpenClaw.Core/           src/OpenClaw.Core/
COPY src/OpenClaw.TokenCollector/ src/OpenClaw.TokenCollector/

RUN dotnet publish src/OpenClaw.TokenCollector/OpenClaw.TokenCollector.csproj \
    -c Release \
    -o /app

# ── Stage 2: Runtime (ASP.NET, not chiseled) ───────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl for the container HEALTHCHECK below.
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV Collector__BindUrl=http://0.0.0.0:8088

EXPOSE 8088

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD ["curl", "-fsS", "http://localhost:8088/health"]

ENTRYPOINT ["dotnet", "OpenClaw.TokenCollector.dll"]
