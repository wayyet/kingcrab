#!/usr/bin/env pwsh
# Content sync (Task D-1): For each content-differs file in diff-*-content.md,
# copy upstream version over kingcrab version, then apply namespace remapping.
#
# Skip rules:
#   - Files in exclusion-list (Native runtime, MafConfigNotices, csproj, etc.)
#   - .csproj / .sln files (handled separately in Task E)
#   - Directory.Build.* (manual review)
#
# Post-processing on each copied file (.cs only):
#   - Replace `using OpenClaw.MicrosoftAgentFrameworkAdapter;` → `using OpenClaw.Agent;`
#   - Replace `using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;` → `using OpenClaw.Agent.A2A;`
#   - Remove lines that only `using OpenClaw.SemanticKernelAdapter;`
#   - Remove lines that only `using OpenClaw.Providers.MicrosoftExtensionsAI;`
#   - Remove lines that only `using OpenClaw.Embeddings.Onnx;`

param(
    [string]$Repo = 'e:\gitee\kingcrab',
    [string]$Upstream = 'E:\GitHub\openclaw.net'
)

$ErrorActionPreference = 'Stop'
Set-Location $Repo

$skipFiles = @(
    'Composition\RuntimeInitializationExtensions.MafConfigNotices.cs',
    'AgentRuntime.cs',
    'NativeAgentRuntimeFactory.cs',
    'Memory\FractalMemoryMcpProvider.cs'
)

$skipExtensions = @('.csproj', '.props', '.targets', '.json', '.bak')

$logDir = Join-Path $Repo 'docs\migration\build-logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$syncLog = Join-Path $logDir 'content-sync.log'
"# Content sync log $(Get-Date -Format o)" | Set-Content $syncLog -Encoding UTF8

$diffDir = Join-Path $Repo 'docs\migration'
$diffs = Get-ChildItem -Path $diffDir -Filter 'diff-*-content.md' -File

$copied = 0
$skipped = 0
$remapped = 0

foreach ($diffFile in $diffs) {
    $proj = $diffFile.BaseName -replace '^diff-', '' -replace '-content$', ''
    $upProjDir = Join-Path $Upstream "src\$proj"
    $kcProjDir = Join-Path $Repo "src\$proj"

    if (-not (Test-Path $upProjDir) -or -not (Test-Path $kcProjDir)) {
        Add-Content -Path $syncLog -Value "[skip-project] $proj"
        continue
    }

    $lines = Get-Content $diffFile.FullName
    foreach ($line in $lines) {
        if ($line -match '^- (.+\.\w+)$') {
            $rel = $matches[1].Trim()
            $ext = [IO.Path]::GetExtension($rel).ToLowerInvariant()
            if ($skipExtensions -contains $ext) {
                Add-Content -Path $syncLog -Value "[skip-ext] $proj/$rel"
                $skipped++
                continue
            }
            if ($skipFiles -contains $rel) {
                Add-Content -Path $syncLog -Value "[skip-name] $proj/$rel"
                $skipped++
                continue
            }
            $upFile = Join-Path $upProjDir $rel
            $kcFile = Join-Path $kcProjDir $rel
            if (-not (Test-Path $upFile)) {
                Add-Content -Path $syncLog -Value "[no-upstream] $proj/$rel"
                $skipped++
                continue
            }
            New-Item -ItemType Directory -Force -Path (Split-Path $kcFile) | Out-Null
            Copy-Item -Path $upFile -Destination $kcFile -Force
            $copied++

            if ($ext -eq '.cs') {
                $content = Get-Content $kcFile -Raw
                $orig = $content
                $content = $content -replace 'using OpenClaw\.MicrosoftAgentFrameworkAdapter\.A2A;', 'using OpenClaw.Agent.A2A;'
                $content = $content -replace 'using OpenClaw\.MicrosoftAgentFrameworkAdapter;', 'using OpenClaw.Agent;'
                # Strip excluded using lines (whole line)
                $content = ($content -split "`r?`n" |
                    Where-Object { $_ -notmatch '^\s*using\s+OpenClaw\.SemanticKernelAdapter\s*;\s*$' } |
                    Where-Object { $_ -notmatch '^\s*using\s+OpenClaw\.Providers\.MicrosoftExtensionsAI(\.[\w]+)?\s*;\s*$' } |
                    Where-Object { $_ -notmatch '^\s*using\s+OpenClaw\.Embeddings\.Onnx(\.[\w]+)?\s*;\s*$' } |
                    Where-Object { $_ -notmatch '^\s*using\s+OpenClaw\.MicrosoftAgentFrameworkAdapter\.[\w.]+\s*;\s*$' }
                ) -join "`r`n"
                if ($content -ne $orig) {
                    Set-Content -Path $kcFile -Value $content -Encoding UTF8 -NoNewline
                    $remapped++
                }
            }
            Add-Content -Path $syncLog -Value "[sync] $proj/$rel"
        }
    }
}

Write-Host "Copied: $copied"
Write-Host "Skipped: $skipped"
Write-Host "Namespace-remapped: $remapped"
Write-Host "Log: $syncLog"
