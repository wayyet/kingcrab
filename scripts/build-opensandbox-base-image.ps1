<#
.SYNOPSIS
    Builds (and optionally pushes) the OpenSandbox BASE image.

.DESCRIPTION
    The base image bundles all OS packages, Node.js, Playwright browser binaries,
    and uv — everything that changes infrequently. Build it once, then use
    build-opensandbox-app-image.ps1 for fast incremental app builds.

.PARAMETER Registry
    Container registry prefix, e.g. ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab

.PARAMETER Tag
    Full tag to apply. Defaults to opensandbox-base-<UTC timestamp YYYYMMddHHmm>.
    Also tags as 'opensandbox-base-latest' when -Push is used.

.PARAMETER Platforms
    Target platform(s). Defaults to linux/amd64 (single platform for local load).
    For multi-platform push, use: -Platforms linux/amd64,linux/arm64 -Push

.PARAMETER Push
    Push the image to the registry after building (enables multi-platform).
    When omitted the image is loaded into the local Docker daemon instead,
    so you can inspect it and docker push manually.

.PARAMETER NoPull
    Skip pulling updated base images (useful for air-gapped builds).

.EXAMPLE
    # Default: build and load into local Docker daemon
    .\build-opensandbox-base-image.ps1

.EXAMPLE
    # Build multi-platform and push to registry
    .\build-opensandbox-base-image.ps1 -Platforms linux/amd64,linux/arm64 -Push
#>
[CmdletBinding()]
param(
    [string]$Registry = "ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab",
    [string]$Tag = "",
    [string[]]$Platforms = @("linux/amd64"),
    [switch]$Push,
    [switch]$NoPull
)

$ErrorActionPreference = "Stop"

if ($Push -and $Platforms.Count -gt 1 -and $Platforms -notcontains "linux/amd64") {
    # no-op: multi-platform + push is valid
}

if (-not $Push -and $Platforms.Count -gt 1) {
    throw "Loading into local daemon requires a single platform. Use -Push for multi-platform builds."
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$dockerfile = Join-Path $repoRoot "Dockerfile.opensandbox.base"

if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile not found: $dockerfile"
}

$gitCommit = "unknown"
try { $gitCommit = (git -C $repoRoot rev-parse --short HEAD).Trim() } catch {}

$timestamp    = [DateTime]::Now.ToString("yyyyMMddHHmm")
$ociTimestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "opensandbox-base-$timestamp"
}

$platformArg = $Platforms -join ","
$builderName = "openclaw-opensandbox-builder"

try {
    docker buildx inspect $builderName *> $null
    if ($LASTEXITCODE -ne 0) { throw "missing" }
} catch {
    docker buildx create --name $builderName --use | Out-Null
}
docker buildx inspect --bootstrap | Out-Null

$fullImage  = "$Registry`:$Tag"
$latestTag  = "$Registry`:opensandbox-base-latest"

$arguments = @(
    "buildx", "build",
    "--file",     $dockerfile,
    "--platform", $platformArg,
    "--label",    "org.opencontainers.image.created=$ociTimestamp",
    "--label",    "org.opencontainers.image.revision=$gitCommit",
    "--tag",      $fullImage
)

if ($Push) {
    # Also push a stable 'latest' alias so app builds can default to it.
    $arguments += @("--tag", $latestTag)
}

if ($NoPull) { $arguments += "--pull=false" }

if ($Push) {
    $arguments += "--push"
} else {
    # Default: load into local Docker daemon so you can docker push manually.
    $arguments += "--load"
}

$arguments += $repoRoot

Write-Host "Building OpenSandbox BASE image"
Write-Host "  Image    : $fullImage"
if ($Push) { Write-Host "  Also tag : $latestTag" }
Write-Host "  Platforms: $platformArg"
Write-Host "  Mode     : $(if ($Push) { 'push' } else { 'load (local)' })"

& docker @arguments

if ($LASTEXITCODE -ne 0) {
    throw "docker buildx build failed."
}

Write-Host ""
Write-Host "Base image ready. To build the app image on top of it, run:"
Write-Host "  .\build-opensandbox-app-image.ps1 -BaseTag $Tag -Push"
