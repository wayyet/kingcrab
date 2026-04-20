[CmdletBinding()]
param(
    [string]$ImageName = "ghcr.io/clawdotnet/openclaw.net-opensandbox",
    [string]$Tag = "latest",
    [string[]]$Platforms = @("linux/amd64", "linux/arm64"),
    [string]$SourceUrl = "",
    [switch]$Push,
    [switch]$Load,
    [switch]$NoPull,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if ($Push -and $Load) {
    throw "-Push and -Load cannot be used together. Choose one output mode."
}

if ($Load -and $Platforms.Count -gt 1) {
    throw "-Load only supports a single platform. Pass -Platforms linux/amd64 (or linux/arm64) with -Load, or use -Push for multi-platform images."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$dockerfile = Join-Path $repoRoot "Dockerfile.opensandbox"

if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile not found: $dockerfile"
}

$gitCommit = "unknown"
try {
    $gitCommit = (git -C $repoRoot rev-parse --short HEAD).Trim()
} catch {
}

$timestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$fullImage = "$ImageName`:$Tag"
$platformArg = $Platforms -join ","

$builderName = "openclaw-opensandbox-builder"

try {
    docker buildx inspect $builderName *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "missing"
    }
} catch {
    docker buildx create --name $builderName --use | Out-Null
}

docker buildx inspect --bootstrap | Out-Null

$arguments = @(
    "buildx", "build",
    "--file", $dockerfile,
    "--platform", $platformArg,
    "--build-arg", "CONFIGURATION=$Configuration",
    "--build-arg", "OPENCLAW_ENABLE_OPENSANDBOX=true",
    "--label", "org.opencontainers.image.created=$timestamp",
    "--label", "org.opencontainers.image.revision=$gitCommit",
    "--tag", $fullImage
)

if ($NoPull) {
    $arguments += "--pull=false"
}

if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
    $arguments += @("--label", "org.opencontainers.image.source=$SourceUrl")
}

if ($Push) {
    $arguments += "--push"
} elseif ($Load) {
    $arguments += "--load"
} else {
    Write-Warning "Neither -Push nor -Load was selected. Build output will stay in build cache only."
}

$arguments += $repoRoot

Write-Host "Building OpenSandbox image: $fullImage"
Write-Host "Platforms: $platformArg"
Write-Host "Mode: $(if ($Push) { 'push' } elseif ($Load) { 'load' } else { 'export disabled' })"
if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
    Write-Host "Source label: $SourceUrl"
}

& docker @arguments

if ($LASTEXITCODE -ne 0) {
    throw "docker buildx build failed."
}