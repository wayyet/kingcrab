<#
.SYNOPSIS
    建立与AI沙箱的WebSocket聊天会话连接，完成认证握手。

.DESCRIPTION
    通过System.Net.WebSockets.ClientWebSocket连接到指定沙箱端点，
    处理auth_required握手，维持KeepAlive连接。返回会话状态JSON。

.PARAMETER WsUrl
    WebSocket地址 (ws:// 或 wss://)

.PARAMETER AuthToken
    认证令牌，支持 env:VAR_NAME 引用

.PARAMETER SystemPrompt
    可选的系统提示词

.PARAMETER Timeout
    连接超时秒数，默认30

.EXAMPLE
    $session = .\Start-SandboxChat.ps1 -WsUrl "ws://sandbox:8080/chat" -AuthToken "env:MY_TOKEN"
#>

param(
    [Parameter(Mandatory)]
    [string]$WsUrl,

    [string]$AuthToken,

    [string]$SystemPrompt = "",

    [int]$Timeout = 30
)

$ErrorActionPreference = "Stop"

function Resolve-Token {
    param([string]$TokenRef)
    if (-not $TokenRef) { return $null }
    if ($TokenRef.StartsWith("env:")) {
        $varName = $TokenRef.Substring(4)
        return [Environment]::GetEnvironmentVariable($varName)
    }
    if ($TokenRef.StartsWith("raw:")) {
        return $TokenRef.Substring(4)
    }
    return $TokenRef
}

function ConvertTo-WsUrl {
    param([string]$Url)
    $uri = [Uri]$Url.TrimEnd('/')
    $scheme = if ($uri.Scheme -eq "https") { "wss" } else { "ws" }
    return [UriBuilder]::new($uri) | ForEach-Object { $_.Scheme = $scheme; $_.Uri.ToString() }
}

try {
    $wsUrl = ConvertTo-WsUrl -Url $WsUrl
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(20)

    $cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    $task = $ws.ConnectAsync([Uri]$wsUrl, $cts.Token)
    $task.Wait()

    # Receive first message (may be auth_required)
    $firstMsg = Receive-WsMessage -WebSocket $ws
    $firstJson = $firstMsg | ConvertFrom-Json

    if ($firstJson.type -eq "auth_required") {
        $token = Resolve-Token -TokenRef $AuthToken
        if (-not $token) {
            throw "Auth required but no token configured"
        }

        $authBody = @{ type = "auth"; access_token = $token } | ConvertTo-Json -Compress
        Send-WsMessage -WebSocket $ws -Message $authBody

        $authReply = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
        if ($authReply.type -ne "auth_ok") {
            throw "Auth failed: $($authReply | ConvertTo-Json)"
        }
    }

    # Emit session info
    $result = @{
        sessionId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
        wsUrl     = $wsUrl
        connected = $ws.State -eq [System.Net.WebSockets.WebSocketState]::Open
        timestamp = (Get-Date -Format "o")
    } | ConvertTo-Json -Compress

    Write-Output $result
}
catch {
    $error = @{ error = $_.Exception.Message; wsUrl = $WsUrl } | ConvertTo-Json -Compress
    Write-Error $error
}
finally {
    if ($ws) { $ws.Dispose() }
    if ($cts) { $cts.Dispose() }
}

function Send-WsMessage {
    param($WebSocket, [string]$Message)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Message)
    $seg = [System.ArraySegment[byte]]::new($bytes)
    $task = $WebSocket.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None)
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
                throw "WebSocket closed by remote"
            }
            $ms.Write($buffer, 0, $result.Result.Count)
            if ($result.Result.EndOfMessage) { break }
        }
        return [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    finally { $ms.Dispose() }
}
