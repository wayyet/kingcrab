<#
.SYNOPSIS
    生成结构化AI评估报告。

.DESCRIPTION
    通过WebSocket连接到评估报告生成沙箱，提交评分数据、测试结果、跟踪摘要和建议，
    获取结构化评估报告JSON。输出符合schemas/evaluation-report.schema.json格式。

.PARAMETER WsUrl
    评估报告生成沙箱WebSocket地址

.PARAMETER Scores
    评分数据JSON字符串，格式: [{"dimension":"...","score":85,"max_score":100,"comment":"..."}]

.PARAMETER TraceSummary
    执行过程跟踪摘要文本

.PARAMETER TestResults
    测试结果JSON字符串

.PARAMETER Recommendations
    改进建议JSON字符串

.PARAMETER OverallComment
    综合评语

.PARAMETER OutputPath
    报告输出路径，默认 stdout

.EXAMPLE
    $scores = '[{"dimension":"功能完整性","score":85,"max_score":100,"comment":"良好"}]'
    .\New-EvaluationReport.ps1 -WsUrl "ws://report:5050/chat" -Scores $scores -OutputPath "./report.json"
#>

param(
    [Parameter(Mandatory)]
    [string]$WsUrl,

    [string]$Scores = "[]",

    [string]$TraceSummary = "",

    [string]$TestResults = "{}",

    [string]$Recommendations = "[]",

    [string]$OverallComment = "",

    [string]$OutputPath,

    [string]$AuthToken,

    [int]$Timeout = 120
)

$ErrorActionPreference = "Stop"

function ConvertTo-WsUrl {
    param([string]$Url)
    $uri = [Uri]$Url.TrimEnd('/')
    $scheme = if ($uri.Scheme -eq "https") { "wss" } else { "ws" }
    return [UriBuilder]::new($uri) | ForEach-Object { $_.Scheme = $scheme; $_.Uri.ToString() }
}

function Resolve-Token {
    param([string]$TokenRef)
    if (-not $TokenRef) { return $null }
    if ($TokenRef.StartsWith("env:")) { return [Environment]::GetEnvironmentVariable($TokenRef.Substring(4)) }
    if ($TokenRef.StartsWith("raw:")) { return $TokenRef.Substring(4) }
    return $TokenRef
}

function Send-WsMessage { param($WebSocket, [string]$Message)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Message)
    $task = $WebSocket.SendAsync([System.ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None)
    $task.Wait()
}

function Receive-WsMessage { param($WebSocket)
    $buffer = [byte[]]::new(65536); $ms = [System.IO.MemoryStream]::new()
    try {
        while ($true) {
            $seg = [System.ArraySegment[byte]]::new($buffer)
            $result = $WebSocket.ReceiveAsync($seg, [System.Threading.CancellationToken]::None)
            $result.Wait()
            if ($result.Result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw "WebSocket closed" }
            $ms.Write($buffer, 0, $result.Result.Count)
            if ($result.Result.EndOfMessage) { break }
        }
        return [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    finally { $ms.Dispose() }
}

function Build-ReportPrompt {
    $prompt = @"
Generate a structured evaluation report based on the following data.
Return as JSON with 'report' key containing:
report_id, evaluated_at, target_endpoint, scores array (each with dimension, score, max_score, comment),
total_score, max_possible_score, overall_rating, strengths array, weaknesses array,
suggestions array (each with area, suggestion, priority), and summary.

Scores: $Scores
Test Results: $TestResults
Trace Summary: $TraceSummary
Recommendations: $Recommendations
Overall Comment: $OverallComment
"@
    return $prompt -replace "`r`n", " "
}

try {
    $wsUrl = ConvertTo-WsUrl -Url $WsUrl
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(20)

    $cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    $ws.ConnectAsync([Uri]$wsUrl, $cts.Token).Wait()

    $firstMsg = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
    if ($firstMsg.type -eq "auth_required") {
        $token = Resolve-Token -TokenRef $AuthToken
        Send-WsMessage -WebSocket $ws -Message (@{ type = "auth"; access_token = $token } | ConvertTo-Json -Compress)
        Receive-WsMessage -WebSocket $ws | Out-Null
    }

    $prompt = Build-ReportPrompt
    $requestId = (Get-Date).Ticks % [int]::MaxValue
    Send-WsMessage -WebSocket $ws -Message (@{ id = $requestId; type = "chat"; prompt = $prompt } | ConvertTo-Json -Compress)

    $reqCts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    $reportJson = ""
    while ($true) {
        $msg = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
        if ($msg.type -eq "result" -and $msg.id -eq $requestId) {
            if ($msg.success) {
                $reportJson = $msg.result | ConvertTo-Json -Compress -Depth 10
            } else {
                throw "Report generation failed: $($msg.error | ConvertTo-Json)"
            }
            break
        }
    }

    if ($OutputPath) {
        $reportJson | Set-Content -Path $OutputPath -Encoding UTF8
        Write-Output (@{ reportPath = $OutputPath; status = "saved" } | ConvertTo-Json -Compress)
    } else {
        Write-Output $reportJson
    }
}
catch {
    Write-Error (@{ error = $_.Exception.Message } | ConvertTo-Json -Compress)
}
finally {
    if ($ws) { $ws.Dispose() }
    if ($cts) { $cts.Dispose() }
    if ($reqCts) { $reqCts.Dispose() }
}
