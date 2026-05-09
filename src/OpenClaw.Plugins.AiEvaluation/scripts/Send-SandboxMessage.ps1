<#
.SYNOPSIS
    向目标AI沙箱发送消息或测试用例，获取响应。

.DESCRIPTION
    通过WebSocket向沙箱发送chat消息（文本或结构化测试用例），等待result响应并返回。

.PARAMETER WsUrl
    目标沙箱WebSocket地址

.PARAMETER AuthToken
    认证令牌，支持 env:VAR_NAME 引用

.PARAMETER Message
    要发送的文本消息

.PARAMETER TestcaseFile
    测试用例JSON文件路径，与Message二选一或组合使用

.PARAMETER Timeout
    请求超时秒数，默认120

.EXAMPLE
    .\Send-SandboxMessage.ps1 -WsUrl "ws://target:9090/chat" -Message "请执行以下测试"
    .\Send-SandboxMessage.ps1 -WsUrl "ws://target:9090/chat" -TestcaseFile "./testcases/login.json"
#>

param(
    [Parameter(Mandatory)]
    [string]$WsUrl,

    [string]$AuthToken,

    [string]$Message,

    [string]$TestcaseFile,

    [int]$Timeout = 120
)

$ErrorActionPreference = "Stop"

# Source shared functions
. "$PSScriptRoot\Start-SandboxChat.ps1" -WsUrl $WsUrl -AuthToken $AuthToken -Timeout $Timeout *>$null

function Build-ChatMessage {
    param([int]$Id, [string]$Prompt)
    $body = @{ id = $Id; type = "chat"; prompt = $Prompt }
    if ($script:SystemPrompt) {
        $body.system_prompt = $script:SystemPrompt
    }
    return $body | ConvertTo-Json -Compress
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
        if ($authReply.type -ne "auth_ok") { throw "Auth failed" }
    }

    # Build prompt
    $prompt = ""
    if ($Message) { $prompt = $Message }
    if ($TestcaseFile) {
        $tcContent = Get-Content $TestcaseFile -Raw -Encoding UTF8
        $prompt += "`nTestcase data: " + $tcContent
    }
    if (-not $prompt) { throw "Either Message or TestcaseFile must be provided" }

    $requestId = (Get-Date).Ticks % [int]::MaxValue
    Send-WsMessage -WebSocket $ws -Message (Build-ChatMessage -Id $requestId -Prompt $prompt)

    # Wait for result
    $reqCts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Timeout))
    while ($true) {
        $msg = Receive-WsMessage -WebSocket $ws | ConvertFrom-Json
        if ($msg.type -eq "result" -and $msg.id -eq $requestId) {
            if ($msg.success) {
                Write-Output ($msg.result | ConvertTo-Json -Compress -Depth 10)
            } else {
                throw "Sandbox error: $($msg.error | ConvertTo-Json)"
            }
            break
        }
        if ($msg.type -eq "error") {
            throw "Sandbox error: $($msg.message)"
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
