[CmdletBinding()]
param(
    [string]$SchemaPath,

    [Parameter(Position = 0)]
    [string[]]$Paths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$validatorPath = Join-Path $repoRoot "src\OpenClaw.Gateway\skills\ontology_extraction\scripts\validate-projection.ps1"

if (-not (Test-Path -LiteralPath $validatorPath)) {
    throw "Validator script not found: $validatorPath"
}

$currentBase = (Get-Location).Path
$invokeArgs = @()

if ($Paths -and $Paths.Count -gt 0) {
    $resolvedPaths = @()
    foreach ($path in $Paths) {
        $resolvedPaths += Resolve-AbsolutePath -Path $path -BasePath $currentBase
    }

    $invokeArgs += ,$resolvedPaths
}

if (-not [string]::IsNullOrWhiteSpace($SchemaPath)) {
    $resolvedSchemaPath = Resolve-AbsolutePath -Path $SchemaPath -BasePath $currentBase
    $invokeArgs += @("-SchemaPath", $resolvedSchemaPath)
}

& $validatorPath @invokeArgs
exit $LASTEXITCODE