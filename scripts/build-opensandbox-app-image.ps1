<#
.SYNOPSIS
    Builds (and optionally pushes) the OpenSandbox APP image using a pre-built base.

.DESCRIPTION
    The app image only runs the .NET build + binary copy on top of the pre-built
    base image, so it finishes much faster than a full from-scratch build.

    The base image is referenced via the -BaseTag parameter (or its full path via
    -BaseImage). Build the base first with build-opensandbox-base-image.ps1.

.PARAMETER Registry
    Container registry prefix, e.g. ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab

.PARAMETER Tag
    Tag for the resulting app image.
    Defaults to opensandbox-<UTC timestamp YYYYMMddHHmm> (matches original format).

.PARAMETER BaseImage
    Full base image reference including tag.
    Overrides -Registry and -BaseTag when specified.

.PARAMETER BaseTag
    Tag of the base image within the same registry.
    Defaults to opensandbox-base-latest.

.PARAMETER Platforms
    Target platform(s). Defaults to linux/amd64 (single platform for local load).
    For multi-platform push, use: -Platforms linux/amd64,linux/arm64 -Push

.PARAMETER Push
    Push the image to the registry after building (enables multi-platform).
    When omitted the image is loaded into the local Docker daemon instead,
    so you can inspect it and docker push manually.

.PARAMETER NoPull
    Skip pulling updated base images (useful for air-gapped builds).

.PARAMETER Configuration
    MSBuild configuration (Release or Debug). Defaults to Release.

.EXAMPLE
    # Default: build and load into local Docker daemon
    .\build-opensandbox-app-image.ps1

.EXAMPLE
    # Pin to a specific base image version
    .\build-opensandbox-app-image.ps1 -BaseTag opensandbox-base-202604162158

.EXAMPLE
    # Build multi-platform and push to registry
    .\build-opensandbox-app-image.ps1 -Platforms linux/amd64,linux/arm64 -Push
#>
[CmdletBinding()]
param(
    [string]$Registry      = "ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab",
    [string]$Tag           = "",
    [string]$BaseImage     = "",
    [string]$BaseTag       = "opensandbox-base-latest",
    [string[]]$Platforms   = @("linux/amd64"),
    [switch]$Push,
    [switch]$NoPull,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if (-not $Push -and $Platforms.Count -gt 1) {
    throw "Loading into local daemon requires a single platform. Use -Push for multi-platform builds."
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$dockerfile = Join-Path $repoRoot "Dockerfile.opensandbox.app"

if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile not found: $dockerfile"
}

$gitCommit = "unknown"
try { $gitCommit = (git -C $repoRoot rev-parse --short HEAD).Trim() } catch {}

$timestamp    = [DateTime]::Now.ToString("yyyyMMddHHmm")
$ociTimestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "opensandbox-$timestamp"
}

# Resolve the full base image reference.
if ([string]::IsNullOrWhiteSpace($BaseImage)) {
    $BaseImage = "$Registry`:$BaseTag"
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

$fullImage = "$Registry`:$Tag"

$arguments = @(
    "buildx", "build",
    "--file",        $dockerfile,
    "--platform",    $platformArg,
    "--build-arg",   "CONFIGURATION=$Configuration",
    "--build-arg",   "OPENCLAW_ENABLE_OPENSANDBOX=true",
    "--build-arg",   "BASE_IMAGE=$BaseImage",
    "--label",       "org.opencontainers.image.created=$ociTimestamp",
    "--label",       "org.opencontainers.image.revision=$gitCommit",
    "--tag",         $fullImage
)

if ($NoPull) { $arguments += "--pull=false" }

if ($Push) {
    $arguments += "--push"
} else {
    # Default: load into local Docker daemon so you can docker push manually.
    $arguments += "--load"
}

$arguments += $repoRoot

Write-Host "Building OpenSandbox APP image"
Write-Host "  Image    : $fullImage"
Write-Host "  Base     : $BaseImage"
Write-Host "  Platforms: $platformArg"
Write-Host "  Mode     : $(if ($Push) { 'push' } else { 'load (local)' })"

& docker @arguments

if ($LASTEXITCODE -ne 0) {
    throw "docker buildx build failed."
}
