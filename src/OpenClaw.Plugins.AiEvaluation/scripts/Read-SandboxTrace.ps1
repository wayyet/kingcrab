<#
.SYNOPSIS
    读取目标AI沙箱的完整执行过程跟踪数据。

.DESCRIPTION
    通过WebSocket连接到trace读取端点，发送trace查询请求，
    获取目标沙箱的思考链路、工具调用、对话内容等执行过程数据。

.PARAMETER WsUrl
    Trace读取端点WebSocket地址

.PARAMETER SessionId
    目标沙箱的会话ID

.PARAMETER TraceType
    跟踪类型: thinking, tool_calls, conversation, all (默认all)

.PARAMETER MaxEntries
    最大返回条目数，默认200

.PARAMETER StepFrom
    起始步骤号

.PARAMETER StepTo
    结束步骤号

.EXAMPLE
    .\Read-SandboxTrace.ps1 -WsUrl "ws://trace:7070/chat" -SessionId "abc123"
    .\Read-SandboxTrace.ps1 -WsUrl "ws://trace:7070/chat" -TraceType "tool_calls" -MaxEntries 50
#>

param(
    [Parameter(Mandatory)]
    [string]$WsUrl,

    [string]$SessionId,

    [ValidateSet("thinking", "tool_calls", "conversation", "all")]
    [string]$TraceType = "all",

    [int]$MaxEntries = 200,

    [int]$StepFrom,

    [int]$StepTo,

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

function Send-WsMessage {
    param($WebSocket, [string]$Message)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Message)
    $task = $WebSocket.SendAsync([System.ArraySegment[byte]]::new($bytes),
        [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None)
    $task.Wait()
}

function Receive-WsMessage {
    param($WebSocket)
    $buffer = [byte[]]::new(65536)
    $ms = [System.IO.MemoryStream]::new()
    try {
        while ($true) {
            $seg = [System.ArraySegment[byte]]::new($buffer)
            $result = $WebSocket.ReceiveAsync($seg, [System.Threading.CancellationToken]::None)
            $result.Wait()
            if ($result.Result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw "WebSocket closed"
            }
            $ms.Write($buffer, 0, $result.Result.Count)
            if ($result.Result.EndOfMessage) { break }
        }
        return [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    finally { $ms.Dispose() }
}

function Build-TraceQuery {
    $filters = @()
    if ($SessionId) { $filters += "session_id=$SessionId" }
    if ($TraceType -ne "all") { $filters += "type=$TraceType" }
    if ($PSBoundParameters.ContainsKey('StepFrom')) { $filters += "step_from=$StepFrom" }
    if ($PSBoundParameters.ContainsKey('StepTo')) { $filters += "step_to=$StepTo" }
    $filterStr = if ($filters.Count -gt 0) { " with filters: $($filters -join ', ')" } else { "" }

    return "Read execution trace$filterStr. Return up to $MaxEntries entries as JSON with 'trace' key containing session_id, source, total_steps, and entries array. Each entry: step, type, content, tool_name, tool_arguments, timestamp."
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
        $authReply = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
    }

    $prompt = Build-TraceQuery
    $requestId = (Get-Date).Ticks % [int]::MaxValue
    Send-WsMessage -WebSocket $ws -Message (@{ id = $requestId; type = "chat"; prompt = $prompt } | ConvertTo-Json -Compress)

    $reqCts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    while ($true) {
        $msg = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
        if ($msg.type -eq "result" -and $msg.id -eq $requestId) {
            if ($msg.success) {
                Write-Output ($msg.result | ConvertTo-Json -Compress -Depth 10)
            } else {
                throw "Trace read failed: $($msg.error | ConvertTo-Json)"
            }
            break
        }
    }
}
catch {
    $error = @{ error = $_.Exception.Message } | ConvertTo-Json -Compress
    Write-Error $error
}
finally {
    if ($ws) { $ws.Dispose() }
    if ($cts) { $cts.Dispose() }
    if ($reqCts) { $reqCts.Dispose() }
}
