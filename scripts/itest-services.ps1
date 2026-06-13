#requires -Version 7
<#
.SYNOPSIS
    kingcrab 集成测试服务一键管理：本地 OpenClaw.Gateway + Kafka/Doris docker 栈。

.DESCRIPTION
    管理三组服务：
      1. 本地 kingcrab 服务（dotnet run --project src/OpenClaw.Gateway，监听 127.0.0.1:18789）
      2. Kafka  docker 容器（kafka-doris-kafka，localhost:9092）
      3. Doris  docker 容器（kafka-doris-fe / kafka-doris-be，FE HTTP 8030 / MySQL 9030，BE 8040）

    docker compose 文件位于：
      C:\Users\wayye\Documents\ai4c_Projects\setting_Install\kafka-doris-deploy\docker-compose.yml

.PARAMETER Action
    restart        停止全部后重新启动（默认）
    start          启动 docker 栈 + 本地 kingcrab
    stop           停止本地 kingcrab + 停止 kafka/doris docker 容器
    stop-kingcrab  仅停止本地 kingcrab 服务
    stop-docker    仅停止 kafka/doris docker 容器
    status         查看各服务状态

.PARAMETER NoKafkaPublish
    启动 kingcrab 时不强制开启 TokenUsageKafka（默认会注入
    OpenClaw__TokenUsageKafka__Enabled=true，便于 token 用量链路集成测试）。

.EXAMPLE
    pwsh scripts/itest-services.ps1                  # 一键重启全部
    pwsh scripts/itest-services.ps1 -Action stop     # 全部停止
    pwsh scripts/itest-services.ps1 -Action status   # 查看状态
#>
param(
    [ValidateSet('restart', 'start', 'stop', 'stop-kingcrab', 'stop-docker', 'status')]
    [string]$Action = 'restart',
    [switch]$NoKafkaPublish
)

$ErrorActionPreference = 'Stop'

$RepoRoot       = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ComposeFile    = 'C:\Users\wayye\Documents\ai4c_Projects\setting_Install\kafka-doris-deploy\docker-compose.yml'
$GatewayProject = Join-Path $RepoRoot 'src\OpenClaw.Gateway'
$GatewayPort    = 18789
$LogDir         = Join-Path $RepoRoot 'logs'
$PidFile        = Join-Path $LogDir 'gateway.pid'
$RoutineLoadJob = 'load_session_token_events'
$RoutineLoadDb  = 'token_metrics'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Wait-Until([scriptblock]$Check, [string]$What, [int]$TimeoutSec = 60, [int]$IntervalSec = 2) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Check) { Write-Host "    [OK] $What" -ForegroundColor Green; return $true } } catch { }
        Start-Sleep -Seconds $IntervalSec
    }
    Write-Warning "$What — 等待超时（${TimeoutSec}s）"
    return $false
}

function Test-GatewayListening {
    $null -ne (Get-NetTCPConnection -LocalPort $GatewayPort -State Listen -ErrorAction SilentlyContinue)
}

function Invoke-DorisSql([string]$Sql) {
    # FE 容器自带 mysql 客户端；失败时返回 $null，由调用方降级处理。
    # 注意：该客户端要求 -h/-P 与取值之间有空格（-h127.0.0.1 会被错误解析成主机 "127"）。
    $output = docker exec kafka-doris-fe mysql -h 127.0.0.1 -P 9030 -uroot --batch -e $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    return $output
}

function Stop-Kingcrab {
    Write-Step '停止本地 kingcrab 服务 (OpenClaw.Gateway)'
    $pids = [System.Collections.Generic.HashSet[int]]::new()
    if (Test-Path $PidFile) {
        $saved = (Get-Content $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($saved -match '^\d+$') { [void]$pids.Add([int]$saved) }
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    }
    Get-Process -Name 'OpenClaw.Gateway' -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add($_.Id) }
    Get-NetTCPConnection -LocalPort $GatewayPort -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { [void]$pids.Add([int]$_.OwningProcess) }

    if ($pids.Count -eq 0) {
        Write-Host '    未发现正在运行的 kingcrab 进程'
        return
    }
    foreach ($processId in $pids) {
        try {
            $proc = Get-Process -Id $processId -ErrorAction Stop
            Write-Host "    Stop-Process $($proc.ProcessName) (PID $processId)"
            Stop-Process -Id $processId -Force -ErrorAction Stop
        } catch { }
    }
    Wait-Until { -not (Test-GatewayListening) } "端口 $GatewayPort 已释放" -TimeoutSec 15 -IntervalSec 1 | Out-Null
}

function Stop-DockerStack {
    Write-Step '停止 Kafka / Doris docker 容器'
    docker compose -f $ComposeFile stop
    if ($LASTEXITCODE -ne 0) { throw 'docker compose stop 失败，请确认 Docker Desktop 已运行' }
}

function Start-DockerStack {
    Write-Step '启动 Kafka / Doris docker 容器'
    docker compose -f $ComposeFile up -d
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up 失败，请确认 Docker Desktop 已运行' }

    Wait-Until {
        docker exec kafka-doris-kafka /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server localhost:9092 *> $null
        $LASTEXITCODE -eq 0
    } 'Kafka broker 就绪 (localhost:9092)' -TimeoutSec 90 | Out-Null

    Wait-Until {
        (Invoke-WebRequest -Uri 'http://localhost:8030/api/bootstrap' -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200
    } 'Doris FE 就绪 (http://localhost:8030)' -TimeoutSec 180 -IntervalSec 3 | Out-Null

    Wait-Until {
        (Invoke-WebRequest -Uri 'http://localhost:8040/api/health' -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200
    } 'Doris BE 就绪 (http://localhost:8040)' -TimeoutSec 180 -IntervalSec 3 | Out-Null

    Resume-RoutineLoadIfPaused
}

function Resume-RoutineLoadIfPaused {
    Write-Step "检查 Doris Routine Load ($RoutineLoadDb.$RoutineLoadJob)"
    $result = Invoke-DorisSql "SHOW ROUTINE LOAD FOR ${RoutineLoadDb}.${RoutineLoadJob}\G"
    if ($null -eq $result) {
        Write-Warning "无法查询 Routine Load 状态。若任务不存在，请先执行 scripts/token-usage/doris-token-metrics.local.sql"
        return
    }
    $stateLine = $result | Where-Object { $_ -match '^\s*State:' } | Select-Object -First 1
    $state = if ($stateLine) { ($stateLine -split ':', 2)[1].Trim() } else { '(未知)' }
    Write-Host "    当前状态: $state"
    if ($state -eq 'PAUSED') {
        Write-Host '    任务处于 PAUSED，执行 RESUME...'
        Invoke-DorisSql "RESUME ROUTINE LOAD FOR ${RoutineLoadDb}.${RoutineLoadJob};" | Out-Null
    }
}

function Start-Kingcrab {
    Write-Step '启动本地 kingcrab 服务 (dotnet run --project src/OpenClaw.Gateway)'
    if (Test-GatewayListening) {
        Write-Warning "端口 $GatewayPort 已被占用，跳过启动（如需重启请用 -Action restart）"
        return
    }
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $env:OpenClaw__TokenUsageKafka__Enabled = if ($NoKafkaPublish) { 'false' } else { 'true' }

    $outLog = Join-Path $LogDir 'gateway.out.log'
    $errLog = Join-Path $LogDir 'gateway.err.log'
    $proc = Start-Process -FilePath 'dotnet' `
        -ArgumentList 'run', '--project', $GatewayProject `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -WindowStyle Hidden -PassThru
    $proc.Id | Set-Content $PidFile
    Write-Host "    PID $($proc.Id)，日志: $outLog"
    Write-Host "    TokenUsageKafka: $($env:OpenClaw__TokenUsageKafka__Enabled)"

    # 首次启动包含编译，给足时间
    if (-not (Wait-Until { Test-GatewayListening } "Gateway 就绪 (127.0.0.1:$GatewayPort)" -TimeoutSec 180 -IntervalSec 3)) {
        Write-Warning "启动失败，最近的错误输出："
        if (Test-Path $errLog) { Get-Content $errLog -Tail 20 | ForEach-Object { Write-Host "    $_" } }
        throw 'kingcrab 服务启动超时'
    }
}

function Show-Status {
    Write-Step 'Docker 容器'
    docker ps -a --filter 'name=kafka-doris' --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'

    Write-Step "本地 kingcrab 服务 (端口 $GatewayPort)"
    if (Test-GatewayListening) {
        $ownerId = (Get-NetTCPConnection -LocalPort $GatewayPort -State Listen | Select-Object -First 1).OwningProcess
        $owner = Get-Process -Id $ownerId -ErrorAction SilentlyContinue
        Write-Host "    [运行中] $($owner.ProcessName) (PID $ownerId)" -ForegroundColor Green
    } else {
        Write-Host '    [未运行]' -ForegroundColor Yellow
    }

    Write-Step 'Doris Routine Load'
    $result = Invoke-DorisSql "SHOW ROUTINE LOAD FOR ${RoutineLoadDb}.${RoutineLoadJob}\G"
    if ($null -ne $result) {
        $result | Where-Object { $_ -match '^\s*(Name|State|Progress|ReasonOfStateChanged):' } |
            ForEach-Object { Write-Host "    $($_.Trim())" }
    } else {
        Write-Host '    (无法查询 — Doris 未运行或任务不存在)'
    }
}

switch ($Action) {
    'stop-kingcrab' { Stop-Kingcrab }
    'stop-docker'   { Stop-DockerStack }
    'stop'          { Stop-Kingcrab; Stop-DockerStack }
    'start'         { Start-DockerStack; Start-Kingcrab; Show-Status }
    'restart'       { Stop-Kingcrab; Stop-DockerStack; Start-DockerStack; Start-Kingcrab; Show-Status }
    'status'        { Show-Status }
}

Write-Host ''
Write-Host "完成: $Action" -ForegroundColor Green
