#requires -Version 7
# Refresh docs/migration/diff-<Project>.md and diff-<Project>-content.md
# by comparing E:\GitHub\openclaw.net\src vs e:\gitee\kingcrab\src for each common project.

[CmdletBinding()]
param(
    [string]$UpstreamRoot = 'E:\GitHub\openclaw.net\src',
    [string]$LocalRoot    = 'E:\gitee\kingcrab\src',
    [string]$DocsDir      = 'E:\gitee\kingcrab\docs\migration'
)

$ErrorActionPreference = 'Stop'

$commonProjects = @(
    'OpenClaw.Agent',
    'OpenClaw.Channels',
    'OpenClaw.Cli',
    'OpenClaw.Client',
    'OpenClaw.Companion',
    'OpenClaw.Core',
    'OpenClaw.Dashboard',
    'OpenClaw.Gateway',
    'OpenClaw.Payments.Abstractions',
    'OpenClaw.Payments.Core',
    'OpenClaw.Payments.StripeLink',
    'OpenClaw.PluginKit',
    'OpenClaw.Plugins.Payment',
    'OpenClaw.SkillKit',
    'OpenClaw.SkillKit.Abstractions',
    'OpenClaw.TestPluginFixtures',
    'OpenClaw.Testing',
    'OpenClaw.Tests',
    'OpenClaw.Tui',
    'OpenClaw.WhatsApp.BaileysWorker',
    'OpenClawNet.Sandbox.OpenSandbox'
)

# File patterns to ignore (build artifacts, runtime data, large binaries)
# Match path segments at start, middle, or end of relative path.
$ignorePattern = '(^|\\)(bin|obj|TestResults|memory|\.vs|\.playwright)(\\|$)'

function Get-RelativeFiles {
    param([string]$Root)
    if (-not (Test-Path $Root)) { return @() }
    Get-ChildItem -Path $Root -Recurse -File |
        Where-Object {
            $rel = $_.FullName.Substring($Root.Length + 1)
            -not ($rel -match $ignorePattern)
        } |
        ForEach-Object {
            $_.FullName.Substring($Root.Length + 1)
        }
}

function Get-FileHashSafe {
    param([string]$Path)
    try {
        return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
    } catch {
        return $null
    }
}

if (-not (Test-Path $DocsDir)) {
    New-Item -Path $DocsDir -ItemType Directory -Force | Out-Null
}

$summary = [System.Collections.Generic.List[pscustomobject]]::new()

foreach ($proj in $commonProjects) {
    $upDir = Join-Path $UpstreamRoot $proj
    $kcDir = Join-Path $LocalRoot $proj

    $upFiles = @(Get-RelativeFiles -Root $upDir)
    $kcFiles = @(Get-RelativeFiles -Root $kcDir)

    $upSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$upFiles, [System.StringComparer]::OrdinalIgnoreCase)
    $kcSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$kcFiles, [System.StringComparer]::OrdinalIgnoreCase)

    $onlyUp = $upFiles | Where-Object { -not $kcSet.Contains($_) } | Sort-Object
    $onlyKc = $kcFiles | Where-Object { -not $upSet.Contains($_) } | Sort-Object
    $common = $upFiles | Where-Object { $kcSet.Contains($_) } | Sort-Object

    # Compute content diffs on common files
    $contentDiffs = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($f in $common) {
        $up = Join-Path $upDir $f
        $kc = Join-Path $kcDir $f
        $hUp = Get-FileHashSafe -Path $up
        $hKc = Get-FileHashSafe -Path $kc
        if ($hUp -and $hKc -and ($hUp -ne $hKc)) {
            $contentDiffs.Add([pscustomobject]@{
                Path     = $f
                Upstream = $hUp
                Local    = $hKc
            })
        }
    }

    # Write diff-<Project>.md
    $diffPath = Join-Path $DocsDir ("diff-{0}.md" -f $proj)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# diff-$proj")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## only-in-openclaw.net ($($onlyUp.Count))")
    foreach ($x in $onlyUp) { [void]$sb.AppendLine("- $x") }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## only-in-kingcrab ($($onlyKc.Count))")
    foreach ($x in $onlyKc) { [void]$sb.AppendLine("- $x") }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## common ($($common.Count))")
    foreach ($x in $common) { [void]$sb.AppendLine("- $x") }
    Set-Content -Path $diffPath -Value $sb.ToString() -Encoding UTF8

    # Write diff-<Project>-content.md
    $contentPath = Join-Path $DocsDir ("diff-{0}-content.md" -f $proj)
    $sb2 = [System.Text.StringBuilder]::new()
    [void]$sb2.AppendLine("# diff-$proj-content")
    [void]$sb2.AppendLine("")
    [void]$sb2.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$sb2.AppendLine("")
    [void]$sb2.AppendLine("## content-differs ($($contentDiffs.Count))")
    foreach ($d in $contentDiffs) {
        [void]$sb2.AppendLine("- $($d.Path)")
        [void]$sb2.AppendLine("  - upstream: $($d.Upstream.Substring(0,12))")
        [void]$sb2.AppendLine("  - local:    $($d.Local.Substring(0,12))")
    }
    Set-Content -Path $contentPath -Value $sb2.ToString() -Encoding UTF8

    $summary.Add([pscustomobject]@{
        Project       = $proj
        OnlyUpstream  = $onlyUp.Count
        OnlyLocal     = $onlyKc.Count
        Common        = $common.Count
        ContentDiffs  = $contentDiffs.Count
    })
}

$summary | Format-Table -AutoSize | Out-String | Write-Host

# Also dump summary as table to docs
$summaryPath = Join-Path $DocsDir 'diff-summary.md'
$sb3 = [System.Text.StringBuilder]::new()
[void]$sb3.AppendLine("# diff-summary")
[void]$sb3.AppendLine("")
[void]$sb3.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb3.AppendLine("")
[void]$sb3.AppendLine("| Project | OnlyUpstream | OnlyLocal | Common | ContentDiffs |")
[void]$sb3.AppendLine("|---|---:|---:|---:|---:|")
foreach ($s in $summary) {
    [void]$sb3.AppendLine(("| {0} | {1} | {2} | {3} | {4} |" -f $s.Project, $s.OnlyUpstream, $s.OnlyLocal, $s.Common, $s.ContentDiffs))
}
Set-Content -Path $summaryPath -Value $sb3.ToString() -Encoding UTF8

Write-Host ""
Write-Host "Summary written to: $summaryPath"
