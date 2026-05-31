#!/usr/bin/env pwsh
# Iterative deletion of only-in-openclaw files that fail to compile due to missing types.
# Strategy: build -> parse CS errors -> intersect with copied list -> delete -> repeat.

param(
    [int]$MaxIter = 30,
    [string]$Repo = 'e:\gitee\kingcrab'
)

$ErrorActionPreference = 'Stop'
Set-Location $Repo

$logDir = Join-Path $Repo 'docs/migration/build-logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

# Load copied list (only files copied via the only-in-openclaw step are eligible for deletion).
$report = Get-Content 'docs/migration/only-in-openclaw-copy-report.md' -Raw
$copiedSet = New-Object System.Collections.Generic.HashSet[string]
foreach ($line in ($report -split "`n")) {
    if ($line -match '^- (OpenClaw[^\s]+)') {
        $rel = $matches[1].Trim()
        $abs = (Join-Path "$Repo\src" $rel) -replace '/', '\'
        [void]$copiedSet.Add($abs.ToLowerInvariant())
    }
}
Write-Host "[init] copied set size = $($copiedSet.Count)"

# Cumulative deletion log
$delLog = Join-Path $logDir 'deleted.txt'
if (Test-Path $delLog) { Remove-Item $delLog -Force }
New-Item -ItemType File -Path $delLog | Out-Null

for ($iter = 1; $iter -le $MaxIter; $iter++) {
    $buildLog = Join-Path $logDir "build-iter-$iter.log"
    Write-Host "[iter $iter] running dotnet build -> $buildLog"
    & dotnet build OpenClaw.Net.slnx --no-restore -c Debug -nologo -v:m 2>&1 | Tee-Object -FilePath $buildLog | Out-Null
    $exit = $LASTEXITCODE
    Write-Host "[iter $iter] dotnet exit=$exit"

    # Parse error lines: "<path>(<line>,<col>): error CSxxxx: ..."
    $errFiles = New-Object System.Collections.Generic.HashSet[string]
    foreach ($line in (Get-Content $buildLog)) {
        if ($line -match '^\s*([A-Za-z]:[^()]+\.cs)\((\d+),(\d+)\):\s*error\s+CS') {
            [void]$errFiles.Add($matches[1].Trim())
        }
    }
    Write-Host "[iter $iter] distinct error files = $($errFiles.Count)"

    if ($exit -eq 0) {
        Write-Host "[iter $iter] BUILD CLEAN"
        break
    }

    # Compute deletable: those that appear in copied set AND still exist on disk.
    $deletable = @()
    foreach ($f in $errFiles) {
        if ($copiedSet.Contains($f.ToLowerInvariant()) -and (Test-Path -LiteralPath $f)) {
            $deletable += $f
        }
    }

    if ($deletable.Count -eq 0) {
        Write-Host "[iter $iter] NO DELETABLE FILES. Errors persist in non-copied files. Stopping."
        Write-Host "[iter $iter] Top 20 error files:"
        $errFiles | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
        break
    }

    Write-Host "[iter $iter] deleting $($deletable.Count) files:"
    foreach ($f in $deletable) {
        Write-Host "  rm $f"
        Remove-Item -LiteralPath $f -Force
        Add-Content -Path $delLog -Value $f
    }
}

Write-Host "DONE. See $delLog for deletion log."
