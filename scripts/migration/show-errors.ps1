#requires -Version 7
# Show unique CS errors from a docs/migration/build-logs/build-iter-N.log.
# When -Iter is omitted, picks the highest-numbered iter log present.
#
# Examples:
#   pwsh scripts/migration/show-errors.ps1                # latest iter
#   pwsh scripts/migration/show-errors.ps1 -Iter 16       # specific iter
#   pwsh scripts/migration/show-errors.ps1 -Top 80        # cap output

[CmdletBinding()]
param(
    [int]$Iter = 0,
    [int]$Top = 0,
    [string]$LogDir = (Join-Path $PSScriptRoot '..\..\docs\migration\build-logs')
)

$ErrorActionPreference = 'Stop'
$LogDir = (Resolve-Path -LiteralPath $LogDir).Path

if ($Iter -le 0) {
    $latest = Get-ChildItem -Path $LogDir -Filter 'build-iter-*.log' |
        ForEach-Object {
            if ($_.BaseName -match 'build-iter-(\d+)') {
                [pscustomobject]@{ Iter = [int]$matches[1]; Path = $_.FullName }
            }
        } |
        Sort-Object Iter -Descending |
        Select-Object -First 1
    if (-not $latest) { throw "no build-iter-*.log found in $LogDir" }
    $Iter = $latest.Iter
    $logPath = $latest.Path
} else {
    $logPath = Join-Path $LogDir "build-iter-$Iter.log"
    if (-not (Test-Path -LiteralPath $logPath)) { throw "log not found: $logPath" }
}

$lines = Get-Content -LiteralPath $logPath
$errs = $lines | Where-Object { $_ -match 'error CS\d+' } | Sort-Object -Unique

Write-Host "[iter-$Iter] $logPath"
Write-Host "total unique error lines: $($errs.Count)"

if ($Top -gt 0 -and $errs.Count -gt $Top) {
    $errs | Select-Object -First $Top | ForEach-Object { Write-Host $_ }
    Write-Host "... ($($errs.Count - $Top) more, use -Top 0 to show all)"
} else {
    $errs | ForEach-Object { Write-Host $_ }
}
