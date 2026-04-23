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
$validatorPath = Join-Path $repoRoot "src\OpenClaw.Gateway\skills\ncrew-ontology\scripts\validate-projection.ps1"

if (-not (Test-Path -LiteralPath $validatorPath)) {
    throw "Validator script not found: $validatorPath"
}

$currentBase = (Get-Location).Path
$defaultSchemaPath = Join-Path $repoRoot "docs\skill-projection-contract-index.schema.json"
$defaultInputPath = Join-Path $repoRoot "src\OpenClaw.Gateway\skills\software-developer\contracts\projections\ncrew-ontology\contract-index.json"
$resolvedPaths = @()

if ($Paths -and $Paths.Count -gt 0) {
    foreach ($path in $Paths) {
        $resolvedPaths += Resolve-AbsolutePath -Path $path -BasePath $currentBase
    }
}
else {
    $resolvedPaths += $defaultInputPath
}

$resolvedSchemaPath = if (-not [string]::IsNullOrWhiteSpace($SchemaPath)) {
    Resolve-AbsolutePath -Path $SchemaPath -BasePath $currentBase
}
else {
    $defaultSchemaPath
}

& $validatorPath $resolvedPaths -SchemaPath $resolvedSchemaPath
exit $LASTEXITCODE