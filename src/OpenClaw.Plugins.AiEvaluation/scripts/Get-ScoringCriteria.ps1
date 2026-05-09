<#
.SYNOPSIS
    查询AI评估的多维度评分标准。

.DESCRIPTION
    通过WebSocket连接到本体知识库端点，查询指定领域和维度的评分标准与等级。
    返回结构化评分标准JSON，符合schemas/scoring-criteria.schema.json格式。

.PARAMETER WsUrl
    本体知识库WebSocket地址

.PARAMETER Domain
    评估领域，如"对话系统"、"代码生成"

.PARAMETER Dimensions
    评分维度数组，默认["功能完整性","交互质量","响应准确性","效率性能"]

.EXAMPLE
    .\Get-ScoringCriteria.ps1 -WsUrl "ws://ontology:6060/chat" -Domain "对话系统"
    .\Get-ScoringCriteria.ps1 -WsUrl "ws://ontology:6060/chat" -Dimensions @("功能完整性","交互质量")
#>

param(
    [Parameter(Mandatory)]
    [string]$WsUrl,

    [string]$Domain = "",

    [string[]]$Dimensions = @("功能完整性", "交互质量", "响应准确性", "效率性能"),

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

    $domainClause = if ($Domain) { " for domain '$Domain'" } else { "" }
    $dimClause = " covering dimensions: $($Dimensions -join ', ')"
    $prompt = "Query evaluation scoring criteria$domainClause$dimClause. Return as JSON with 'criteria' key containing domain, version, and dimensions array. Each dimension: name, description, max_score, indicators array, levels array (each level: label, range_min, range_max, description)."

    $requestId = (Get-Date).Ticks % [int]::MaxValue
    Send-WsMessage -WebSocket $ws -Message (@{ id = $requestId; type = "chat"; prompt = $prompt } | ConvertTo-Json -Compress)

    $reqCts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    while ($true) {
        $msg = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
        if ($msg.type -eq "result" -and $msg.id -eq $requestId) {
            if ($msg.success) { Write-Output ($msg.result | ConvertTo-Json -Compress -Depth 10) }
            else { throw "Query failed: $($msg.error | ConvertTo-Json)" }
            break
        }
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
