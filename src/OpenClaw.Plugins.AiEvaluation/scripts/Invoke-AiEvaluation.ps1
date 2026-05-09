<#
.SYNOPSIS
    一站式AI评估流程编排脚本。

.DESCRIPTION
    按照标准五步流程自动执行完整AI评估：
    1. 获取测试用例 → 2. 发送至目标沙箱 → 3. 读取执行跟踪 →
    4. 查询评分标准 → 5. 生成评估报告

.PARAMETER ConfigPath
    评估配置文件路径 (JSON)

.PARAMETER TestcasePath
    测试用例文件或目录路径

.PARAMETER OutputDir
    输出目录，中间结果和最终报告保存位置

.EXAMPLE
    .\Invoke-AiEvaluation.ps1 -ConfigPath "./evaluation-config.json" -OutputDir "./reports/"
#>

param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,

    [string]$TestcasePath = "",

    [Parameter(Mandatory)]
    [string]$OutputDir
)

$ErrorActionPreference = "Continue"
$scriptDir = $PSScriptRoot

Write-Host "=== AI Evaluation Pipeline ===" -ForegroundColor Cyan
Write-Host "Config: $ConfigPath"
Write-Host "Output: $OutputDir"
Write-Host ""

# Load config
if (-not (Test-Path $ConfigPath)) {
    Write-Error "Config file not found: $ConfigPath"
    exit 1
}
$config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json

# Create output directory
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stepResults = @{}

# ─── Step 1: Fetch Testcases ───
Write-Host "[Step 1/5] Fetching test cases..." -ForegroundColor Yellow
try {
    if ($TestcasePath -and (Test-Path $TestcasePath)) {
        Write-Host "  Using provided test cases from $TestcasePath"
        if (Test-Path $TestcasePath -PathType Container) {
            $testcases = Get-ChildItem $TestcasePath -Filter "*.json" | ForEach-Object {
                Get-Content $_.FullName -Raw -Encoding UTF8
            }
        } else {
            $testcases = @(Get-Content $TestcasePath -Raw -Encoding UTF8)
        }
    } elseif ($config.endpoints.generator.wsUrl) {
        $result = & "$scriptDir\Send-SandboxMessage.ps1" `
            -WsUrl $config.endpoints.generator.wsUrl `
            -AuthToken $config.endpoints.generator.authToken `
            -Message "Generate structured test cases for evaluation." `
            -Timeout $config.evaluation.timeoutSeconds 2>&1
        $testcases = @($result | Where-Object { $_ -notmatch "^Write-Error" })
    } else {
        Write-Host "  No generator endpoint configured, skipping"
        $testcases = @()
    }
    $stepResults.testcases = $testcases
    Write-Host "  Got $($testcases.Count) test case(s)" -ForegroundColor Green
} catch {
    Write-Host "  Step 1 failed: $_" -ForegroundColor Red
}

# ─── Step 2: Send to Target ───
Write-Host "[Step 2/5] Sending test cases to target sandbox..." -ForegroundColor Yellow
$targetResponses = @()
try {
    if ($config.endpoints.target.wsUrl) {
        foreach ($tc in $testcases) {
            $tcFile = "$OutputDir/tc-$($stepResponses.Count).json"
            $tc | Set-Content -Path $tcFile -Encoding UTF8
            $response = & "$scriptDir\Send-SandboxMessage.ps1" `
                -WsUrl $config.endpoints.target.wsUrl `
                -AuthToken $config.endpoints.target.authToken `
                -TestcaseFile $tcFile `
                -Timeout $config.endpoints.target.requestTimeoutSeconds 2>&1
            $targetResponses += $response | Where-Object { $_ -notmatch "^Write-Error" }
        }
    }
    $stepResults.targetResponses = $targetResponses
    Write-Host "  Got $($targetResponses.Count) response(s)" -ForegroundColor Green
} catch {
    Write-Host "  Step 2 failed: $_" -ForegroundColor Red
}

# ─── Step 3: Read Trace ───
Write-Host "[Step 3/5] Reading execution trace..." -ForegroundColor Yellow
try {
    if ($config.endpoints.trace.wsUrl) {
        $trace = & "$scriptDir\Read-SandboxTrace.ps1" `
            -WsUrl $config.endpoints.trace.wsUrl `
            -AuthToken $config.endpoints.trace.authToken `
            -MaxEntries 200 `
            -Timeout $config.evaluation.timeoutSeconds 2>&1
        $stepResults.trace = $trace | Where-Object { $_ -notmatch "^Write-Error" }
        $stepResults.trace | Set-Content -Path "$OutputDir/trace-$timestamp.json" -Encoding UTF8
    }
    Write-Host "  Trace saved" -ForegroundColor Green
} catch {
    Write-Host "  Step 3 failed: $_" -ForegroundColor Red
}

# ─── Step 4: Query Scoring Criteria ───
Write-Host "[Step 4/5] Querying scoring criteria..." -ForegroundColor Yellow
try {
    if ($config.endpoints.ontology.wsUrl) {
        $criteria = & "$scriptDir\Get-ScoringCriteria.ps1" `
            -WsUrl $config.endpoints.ontology.wsUrl `
            -AuthToken $config.endpoints.ontology.authToken `
            -Timeout $config.evaluation.timeoutSeconds 2>&1
        $stepResults.criteria = $criteria | Where-Object { $_ -notmatch "^Write-Error" }
        $stepResults.criteria | Set-Content -Path "$OutputDir/criteria-$timestamp.json" -Encoding UTF8
    }
    Write-Host "  Criteria saved" -ForegroundColor Green
} catch {
    Write-Host "  Step 4 failed: $_" -ForegroundColor Red
}

# ─── Step 5: Generate Report ───
Write-Host "[Step 5/5] Generating evaluation report..." -ForegroundColor Yellow
try {
    if ($config.endpoints.evalReport.wsUrl) {
        $report = & "$scriptDir\New-EvaluationReport.ps1" `
            -WsUrl $config.endpoints.evalReport.wsUrl `
            -AuthToken $config.endpoints.evalReport.authToken `
            -TraceSummary "See trace-$timestamp.json" `
            -OutputPath "$OutputDir/evaluation-$timestamp.json" `
            -Timeout $config.evaluation.timeoutSeconds 2>&1
        $stepResults.report = $report | Where-Object { $_ -notmatch "^Write-Error" }
    }
    Write-Host "  Report generated" -ForegroundColor Green
} catch {
    Write-Host "  Step 5 failed: $_" -ForegroundColor Red
}

# ─── Summary ───
$summary = @{
    pipeline   = "ai-evaluation"
    timestamp  = $timestamp
    configPath = $ConfigPath
    outputDir  = $OutputDir
    steps      = @{
        testcases       = @{ count = $stepResults.testcases.Count }
        targetResponses = @{ count = $stepResults.targetResponses.Count }
        trace           = @{ saved = [bool]($stepResults.trace) }
        criteria        = @{ saved = [bool]($stepResults.criteria) }
        report          = @{ path = "$OutputDir/evaluation-$timestamp.json" }
    }
}

$summary | ConvertTo-Json -Depth 3 | Set-Content -Path "$OutputDir/pipeline-summary-$timestamp.json" -Encoding UTF8
Write-Host ""
Write-Host "=== Pipeline Complete ===" -ForegroundColor Cyan
Write-Output ($summary | ConvertTo-Json -Depth 3)
