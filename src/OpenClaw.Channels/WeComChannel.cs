using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// 企业微信（WeCom）智能机器人通道适配器。
/// 使用 WebSocket 长连接模式接收消息（无需公网回调 URL），
/// 通过 REST API 发送消息和上传媒体。
/// 支持运行时热重载配置。
/// </summary>
public sealed class WeComChannel : IChannelAdapter, IRestartableChannelAdapter
{
    // ── 企业微信 API 地址 ──
    /// <summary>智能机器人 WebSocket 长连接地址</summary>
    private const string WeComWsUrl = "wss://openws.work.weixin.qq.com";

    /// <summary>企业微信 REST API 基础地址</summary>
    private const string WeComApiBase = "https://qyapi.weixin.qq.com";

    // ── 心跳 ──
    /// <summary>建议每 30 秒发送一次 ping 保活</summary>
    private const int HeartbeatIntervalMs = 30_000;

    private readonly WeComChannelConfig _initialConfig;
    private readonly HttpClient _http;
    private readonly ILogger<WeComChannel> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    // 运行时覆盖配置（通过 UpdateConfigAsync 设置，优先于 appsettings）
    private volatile WeComChannelConfig? _runtimeOverride;

    // ── WebSocket 长连接凭证 ──
    private string? _botId;
    private string? _botSecret;

    // ── REST API 凭证 ──
    private string? _corpId;
    private int _agentId;
    private string? _corpSecret;

    // ── Access Token 缓存 ──
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // ── 连接生命周期 ──
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private CancellationToken _appLifetime = CancellationToken.None;

    // ── 当前活跃的 WebSocket 连接（用于发送回复） ──
    private volatile ClientWebSocket? _activeWs;

    // ── WebSocket 写出锁，防止并发写 ──
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    // ── 入站消息上下文缓存（用于快速 WebSocket 回复） ──
    // key: chatid 或 userid, value: 最近一次消息的上下文信息
    private readonly ConcurrentDictionary<string, InboundMsgContext> _inboundContexts = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存最近一次入站消息的上下文，用于 WebSocket 快速回复。
    /// </summary>
    private sealed record InboundMsgContext(string MsgId, string? ChatId, string? UserId, DateTimeOffset ReceivedAt);

    public WeComChannel(
        WeComChannelConfig initialConfig,
        ILogger<WeComChannel> logger)
    {
        _initialConfig = initialConfig;
        _logger = logger;
        _http = HttpClientFactory.Create();
    }

    public string ChannelId => "wecom";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    /// <summary>获取当前生效的配置（运行时覆盖优先）</summary>
    public WeComChannelConfig GetEffectiveConfig() => _runtimeOverride ?? _initialConfig;

    /// <summary>设置运行时配置覆盖</summary>
    public void SetRuntimeConfig(WeComChannelConfig? cfg) => _runtimeOverride = cfg;

    /// <summary>热更新配置并重新连接</summary>
    public async Task UpdateConfigAsync(WeComChannelConfig newConfig, CancellationToken ct = default)
    {
        SetRuntimeConfig(newConfig);
        _logger.LogInformation("企业微信配置已通过 API 热更新，正在重新连接...");
        await RestartAsync(ct);
    }

    // ════════════════════════════ 生命周期 ════════════════════════════

    public async Task StartAsync(CancellationToken ct)
    {
        _appLifetime = ct;

        var cfg = GetEffectiveConfig();
        if (!cfg.Enabled)
        {
            _logger.LogInformation("企业微信通道已禁用；设置 Enabled=true 或通过管理 API 启用以激活。");
            return;
        }

        ResolveCredentials(cfg);
        if (!ValidateWsCredentials())
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunWsLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>重新连接（用于配置热重载）</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            // 取消当前接收循环
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                if (_receiveLoop is not null)
                {
                    try { await _receiveLoop; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogDebug(ex, "企业微信接收循环在重启时退出。"); }
                }
                _cts.Dispose();
                _cts = null;
                _receiveLoop = null;
            }

            var cfg = GetEffectiveConfig();
            if (!cfg.Enabled)
            {
                _logger.LogInformation("企业微信通道在配置热重载后已禁用。");
                return;
            }

            ResolveCredentials(cfg);
            if (!ValidateWsCredentials())
                return;

            // 清除缓存状态
            _accessToken = null;
            _tokenExpiry = DateTimeOffset.MinValue;
            _inboundContexts.Clear();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime);
            _receiveLoop = RunWsLoopAsync(_cts.Token);
            _logger.LogInformation("企业微信通道已使用新配置重新连接。");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>解析凭证（SecretRef 或明文值）</summary>
    private void ResolveCredentials(WeComChannelConfig cfg)
    {
        _botId = SecretResolver.Resolve(cfg.BotIdRef) ?? cfg.BotId;
        _botSecret = SecretResolver.Resolve(cfg.BotSecretRef) ?? cfg.BotSecret;
        _corpId = SecretResolver.Resolve(cfg.CorpIdRef) ?? cfg.CorpId;
        _corpSecret = SecretResolver.Resolve(cfg.CorpSecretRef) ?? cfg.CorpSecret;

        var agentIdStr = SecretResolver.Resolve(cfg.AgentIdRef);
        _agentId = int.TryParse(agentIdStr, out var aid) ? aid : cfg.AgentId;
    }

    /// <summary>校验 WebSocket 长连接必需的凭证</summary>
    private bool ValidateWsCredentials()
    {
        if (string.IsNullOrWhiteSpace(_botId) || string.IsNullOrWhiteSpace(_botSecret))
        {
            _logger.LogError("企业微信 BotId 或 BotSecret 未配置；通道无法启动。");
            return false;
        }
        return true;
    }

    /// <summary>校验 REST API 所需的凭证是否完整</summary>
    private bool HasApiCredentials()
        => !string.IsNullOrWhiteSpace(_corpId) && !string.IsNullOrWhiteSpace(_corpSecret);

    // ════════════════════════════ WebSocket 长连接循环 ════════════════════════════

    /// <summary>
    /// WebSocket 主循环：连接 → 鉴权 → 接收消息 → 断开重连。
    /// 使用指数退避策略，初始 2s，最大 60s。
    /// </summary>
    private async Task RunWsLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        const int maxBackoffSec = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try
                {
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await ws.ConnectAsync(new Uri(WeComWsUrl), ct);
                    _logger.LogInformation("企业微信 WebSocket 已连接。");

                    // 保存活跃连接引用，供 SendAsync 发送回复使用
                    _activeWs = ws;

                    // 连接成功后发送订阅帧进行鉴权
                    await SendSubscribeAsync(ws, ct);

                    backoff = TimeSpan.FromSeconds(2);
                    // 进入消息处理循环（含心跳）
                    await ProcessWsMessagesAsync(ws, ct);
                }
                finally
                {
                    var oldWs = Interlocked.Exchange(ref _activeWs, null);
                    try { oldWs?.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "企业微信 WebSocket 连接错误，{Sec} 秒后重连。", backoff.TotalSeconds);
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSec));
        }
    }

    /// <summary>
    /// 发送 aibot_subscribe 帧完成鉴权。
    /// 格式：{"cmd":"aibot_subscribe","headers":{"req_id":"..."},"body":{"bot_id":"...","secret":"..."}}
    /// </summary>
    private async Task SendSubscribeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var subscribeMsg = new WeComWsRequest
        {
            Cmd = "aibot_subscribe",
            Headers = new WeComWsRequestHeaders { ReqId = Guid.NewGuid().ToString("N") },
            Body = new WeComSubscribeBody
            {
                BotId = _botId!,
                Secret = _botSecret!
            }
        };

        var json = JsonSerializer.Serialize(subscribeMsg, WeComJsonContext.Default.WeComWsRequest);
        await SendWsTextDirectAsync(ws, json, ct);
        _logger.LogInformation("企业微信 aibot_subscribe 已发送，等待消息推送。");
    }

    /// <summary>
    /// 处理 WebSocket 消息循环：接收完整帧 → 分发到 HandleWsFrameAsync。
    /// 同时管理心跳定时器。
    /// </summary>
    private async Task ProcessWsMessagesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        var lastPing = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // 检查是否需要发送心跳
            if (DateTimeOffset.UtcNow - lastPing > TimeSpan.FromMilliseconds(HeartbeatIntervalMs))
            {
                await SendPingAsync(ws, ct);
                lastPing = DateTimeOffset.UtcNow;
            }

            buffer.Clear();
            ValueWebSocketReceiveResult result;

            // 读取完整帧
            do
            {
                var mem = buffer.GetMemory(8192);
                // 设置接收超时为心跳间隔的 2 倍，避免永久阻塞
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HeartbeatIntervalMs * 2);
                try
                {
                    result = await ws.ReceiveAsync(mem, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 超时，发心跳并继续
                    await SendPingAsync(ws, ct);
                    lastPing = DateTimeOffset.UtcNow;
                    result = new ValueWebSocketReceiveResult(0, WebSocketMessageType.Text, true);
                    break;
                }

                buffer.Advance(result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (buffer.WrittenCount == 0)
                continue;

            if (result.MessageType != WebSocketMessageType.Text)
            {
                _logger.LogDebug("企业微信 WebSocket 收到非文本帧，已跳过。");
                continue;
            }

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            try
            {
                await HandleWsFrameAsync(ws, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "企业微信 WebSocket 帧处理异常：{Json}", json);
            }
        }
    }

    /// <summary>
    /// 分发 WebSocket 帧：按 cmd 字段路由到对应的处理函数。
    /// </summary>
    private async Task HandleWsFrameAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cmd = GetString(root, "cmd");
        var reqId = GetReqId(root);

        switch (cmd)
        {
            // ── 消息回调 ──
            case "aibot_msg_callback":
                await HandleMsgCallbackAsync(ws, root, reqId, ct);
                return;

            // ── 事件回调 ──
            case "aibot_event_callback":
                await HandleEventCallbackAsync(ws, root, reqId, ct);
                return;

            // ── 心跳响应 ──
            case "aibot_pong":
                _logger.LogDebug("企业微信 WebSocket pong 响应。");
                return;

            default:
                _logger.LogDebug("企业微信 WebSocket 未知 cmd={Cmd}，已忽略。", cmd);
                return;
        }
    }

    // ════════════════════════════ 消息处理 ════════════════════════════

    /// <summary>
    /// 处理 aibot_msg_callback：解析消息体 → 过滤 → 构建 InboundMessage → 触发 OnMessageReceived。
    /// </summary>
    private async Task HandleMsgCallbackAsync(ClientWebSocket ws, JsonElement root, string? reqId, CancellationToken ct)
    {
        if (!root.TryGetProperty("body", out var body))
        {
            _logger.LogWarning("企业微信消息回调缺少 body 字段。");
            return;
        }

        var cfg = GetEffectiveConfig();
        var msgId = GetString(body, "msgid");
        var chatId = GetString(body, "chatid");
        var chatType = GetString(body, "chattype"); // "group" 或 "single"
        var msgType = GetString(body, "msgtype") ?? "text";

        // 解析发送者信息
        string? senderId = null;
        if (body.TryGetProperty("from", out var fromProp))
            senderId = GetString(fromProp, "userid");

        // 提取消息文本
        var text = ReadWeComText(body);

        if (string.IsNullOrWhiteSpace(text) && msgType != "image" && msgType != "file" && msgType != "voice" && msgType != "video")
        {
            // 无文本且非媒体消息，已读不回
            return;
        }

        if (string.IsNullOrWhiteSpace(senderId))
        {
            _logger.LogDebug("企业微信消息缺少发送者 userid，已丢弃。");
            return;
        }

        var isGroup = string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase);

        // ── 群聊策略过滤 ──
        if (isGroup)
        {
            if (string.Equals(cfg.GroupPolicy, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("企业微信群消息已丢弃（GroupPolicy=disabled）。");
                return;
            }
            if (string.Equals(cfg.GroupPolicy, "allowlist", StringComparison.OrdinalIgnoreCase) &&
                !IsGroupAllowed(chatId, cfg))
            {
                _logger.LogDebug("企业微信群 {ChatId} 不在白名单中，消息已丢弃。", chatId);
                return;
            }
        }

        // ── 发信人白名单过滤 ──
        if (!IsUserAllowed(senderId, cfg))
        {
            _logger.LogDebug("企业微信用户 {UserId} 不在白名单中，消息已丢弃。", senderId);
            return;
        }

        // ── 群聊 @提及 过滤 ──
        if (isGroup && cfg.RequireMentionInGroup && !IsBotMentioned(body))
        {
            _logger.LogDebug("企业微信群消息未 @机器人 且 RequireMentionInGroup=true，已丢弃。");
            return;
        }

        // ── 文本截断 ──
        if (text is not null && text.Length > cfg.MaxInboundChars)
            text = text[..cfg.MaxInboundChars];

        // ── 缓存入站上下文（用于后续 WebSocket 快速回复） ──
        CacheInboundContext(msgId, chatId, senderId);

        // ── 处理图片/文件/语音/视频媒体 ──
        var mediaMarker = BuildMediaMarker(body, msgType);
        var finalText = text;
        if (!string.IsNullOrWhiteSpace(mediaMarker) && !string.IsNullOrWhiteSpace(finalText))
            finalText = finalText + "\n" + mediaMarker;
        else if (!string.IsNullOrWhiteSpace(mediaMarker))
            finalText = mediaMarker;

        // ── 构建 InboundMessage ──
        var inbound = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            Text = finalText ?? "",
            MessageId = msgId,
            IsGroup = isGroup,
            GroupId = isGroup ? chatId : null,
            MediaType = msgType,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(inbound, ct);
    }

    /// <summary>
    /// 处理 aibot_event_callback：
    /// - enter_chat：用户首次进入单聊，发送欢迎语（5 秒内回复）
    /// - template_card_event / feedback_event / disconnected_event：仅记录日志
    /// </summary>
    private async Task HandleEventCallbackAsync(ClientWebSocket ws, JsonElement root, string? reqId, CancellationToken ct)
    {
        if (!root.TryGetProperty("body", out var body))
            return;

        var eventType = GetString(body, "eventtype");

        switch (eventType)
        {
            case "enter_chat":
                // 用户首次进入机器人单聊，可发送欢迎语
                _logger.LogInformation("企业微信 enter_chat 事件，用户进入了单聊会话。");
                break;

            case "disconnected_event":
                _logger.LogInformation("企业微信 disconnected_event：当前连接被新连接踢掉，将自动重连。");
                break;

            default:
                _logger.LogDebug("企业微信事件 {EventType} 已记录。", eventType);
                break;
        }
    }

    /// <summary>缓存入站消息上下文，用于后续 WebSocket 快速回复</summary>
    private void CacheInboundContext(string? msgId, string? chatId, string? senderId)
    {
        if (string.IsNullOrWhiteSpace(msgId))
            return;

        var ctx = new InboundMsgContext(msgId, chatId, senderId, DateTimeOffset.UtcNow);

        // 同时以 chatid 和 userid 为 key 缓存
        if (!string.IsNullOrWhiteSpace(chatId))
            _inboundContexts[chatId] = ctx;
        if (!string.IsNullOrWhiteSpace(senderId))
            _inboundContexts[senderId] = ctx;
    }

    /// <summary>
    /// 尝试获取可用于 WebSocket 回复的上下文。
    /// 企业微信允许在 24 小时内回复最后一条用户消息。
    /// </summary>
    private bool TryGetInboundContext(string recipientId, out InboundMsgContext ctx)
    {
        ctx = null!;
        if (!_inboundContexts.TryGetValue(recipientId, out var found))
            return false;

        // 超过 24 小时的上下文不可用
        if (DateTimeOffset.UtcNow - found.ReceivedAt > TimeSpan.FromHours(24))
        {
            _inboundContexts.TryRemove(recipientId, out _);
            return false;
        }

        ctx = found;
        return true;
    }

    /// <summary>提取企业微信消息文本</summary>
    private static string? ReadWeComText(JsonElement body)
    {
        // 纯文本消息
        if (body.TryGetProperty("text", out var textProp) &&
            textProp.ValueKind == JsonValueKind.Object)
        {
            var content = GetString(textProp, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return content;
        }

        // mixed 消息（图文混排）：提取所有 text 类型的 item
        if (body.TryGetProperty("mixed", out var mixedProp) &&
            mixedProp.ValueKind == JsonValueKind.Object &&
            mixedProp.TryGetProperty("msg_item", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in items.EnumerateArray())
            {
                var itemType = GetString(item, "msgtype");
                if (string.Equals(itemType, "text", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("text", out var itemText) &&
                    itemText.ValueKind == JsonValueKind.Object)
                {
                    var itemContent = GetString(itemText, "content");
                    if (!string.IsNullOrWhiteSpace(itemContent))
                        sb.Append(itemContent);
                }
            }
            if (sb.Length > 0)
                return sb.ToString();
        }

        return null;
    }

    /// <summary>
    /// 构建媒体标记。企业微信图片/文件/语音/视频消息会携带 media_id，
    /// 我们将其转换为 [IMAGE:...] / [FILE:...] 等标记供下游处理。
    /// </summary>
    private static string? BuildMediaMarker(JsonElement body, string msgType)
    {
        return msgType switch
        {
            "image" => body.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object
                ? $"[IMAGE:wecom:{GetString(img, "media_id") ?? "unknown"}]"
                : "[IMAGE:wecom:unknown]",

            "file" => body.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object
                ? $"[FILE:wecom:{GetString(file, "media_id") ?? "unknown"}]"
                : "[FILE:wecom:unknown]",

            "voice" => body.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object
                ? $"[VOICE:wecom:{GetString(voice, "media_id") ?? "unknown"}]"
                : "[VOICE:wecom:unknown]",

            "video" => body.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object
                ? $"[VIDEO:wecom:{GetString(video, "media_id") ?? "unknown"}]"
                : "[VIDEO:wecom:unknown]",

            _ => null
        };
    }

    /// <summary>检查群聊消息中是否 @了机器人。企业微信的 text.content 中包含 @BotName 格式。</summary>
    private static bool IsBotMentioned(JsonElement body)
    {
        if (body.TryGetProperty("text", out var textProp) &&
            textProp.ValueKind == JsonValueKind.Object)
        {
            var content = GetString(textProp, "content") ?? "";
            // 企业微信群聊 @机器人 时消息内容包含 @机器人名称
            return content.Contains('@');
        }
        return false;
    }

    // ════════════════════════════ 发送消息 ════════════════════════════

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            return;

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);

            // 优先尝试通过 WebSocket 回复（24h 内有效）
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                var sentViaWs = await TrySendTextViaWsAsync(outbound.RecipientId, remaining, ct);
                if (!sentViaWs)
                {
                    // 回退到 REST API 主动发送
                    if (!HasApiCredentials())
                    {
                        _logger.LogWarning("企业微信 REST API 凭证未配置，无法主动发送消息到 {RecipientId}。", outbound.RecipientId);
                    }
                    else
                    {
                        await RefreshAccessTokenAsync(ct);
                        await SendTextViaApiAsync(outbound.RecipientId, remaining, ct);
                    }
                }
            }

            // 发送媒体（图片/文件等）
            if (markers.Count > 0 && HasApiCredentials())
            {
                foreach (var marker in markers)
                {
                    try
                    {
                        await RefreshAccessTokenAsync(ct);
                        await SendMarkerViaApiAsync(outbound.RecipientId, marker, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "发送企业微信媒体标记失败 {Kind}={Value}。",
                            marker.Kind, marker.Value);
                    }
                }
            }

            // 兜底：文本和标记都为空时，直接发原文
            if (string.IsNullOrWhiteSpace(remaining) && markers.Count == 0)
            {
                var sentViaWs = await TrySendTextViaWsAsync(outbound.RecipientId, outbound.Text, ct);
                if (!sentViaWs && HasApiCredentials())
                {
                    await RefreshAccessTokenAsync(ct);
                    await SendTextViaApiAsync(outbound.RecipientId, outbound.Text, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送企业微信消息到 {RecipientId} 失败。", outbound.RecipientId);
        }
    }

    /// <summary>尝试通过 WebSocket 回复文本消息（需要在 24h 内有入站消息）</summary>
    private async Task<bool> TrySendTextViaWsAsync(string recipientId, string text, CancellationToken ct)
    {
        if (!TryGetInboundContext(recipientId, out var ctx))
            return false;

        // 构建回复帧
        var replyMsg = new WeComWsRequest
        {
            Cmd = "aibot_respond_msg",
            Headers = new WeComWsRequestHeaders { ReqId = Guid.NewGuid().ToString("N") },
            Body = new WeComRespondBody
            {
                MsgId = ctx.MsgId,
                MsgType = "message",
                Stream = new WeComStreamInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Finish = true,
                    Content = text
                }
            }
        };

        var json = JsonSerializer.Serialize(replyMsg, WeComJsonContext.Default.WeComWsRequest);
        await SendWsTextAsync(json, ct);
        return true;
    }

    /// <summary>通过 REST API 发送文本消息</summary>
    private async Task SendTextViaApiAsync(string recipientId, string text, CancellationToken ct)
    {
        const int maxBytes = 2048;
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = TruncateToMaxUtf8Bytes(text, maxBytes);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["touser"] = recipientId,
            ["msgtype"] = "text",
            ["agentid"] = _agentId,
            ["text"] = new Dictionary<string, object> { ["content"] = text }
        };

        var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
        await PostApiAsync("/cgi-bin/message/send", json, ct);
    }

    /// <summary>通过 REST API 发送媒体标记（图片/文件）</summary>
    private async Task SendMarkerViaApiAsync(string recipientId, MediaMarker marker, CancellationToken ct)
    {
        switch (marker.Kind)
        {
            case MediaMarkerKind.ImagePath:
            case MediaMarkerKind.ImageUrl:
                {
                    var data = await FetchMediaBytesAsync(marker, ct);
                    if (data is null) return;
                    var mediaId = await UploadMediaAsync(data, "image", ct);
                    if (mediaId is null) return;

                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["touser"] = recipientId,
                        ["msgtype"] = "image",
                        ["agentid"] = _agentId,
                        ["image"] = new Dictionary<string, object> { ["media_id"] = mediaId }
                    };
                    var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
                    await PostApiAsync("/cgi-bin/message/send", json, ct);
                    break;
                }

            case MediaMarkerKind.FilePath:
            case MediaMarkerKind.FileUrl:
                {
                    var data = await FetchMediaBytesAsync(marker, ct);
                    if (data is null) return;
                    var mediaId = await UploadMediaAsync(data, "file", ct);
                    if (mediaId is null) return;

                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["touser"] = recipientId,
                        ["msgtype"] = "file",
                        ["agentid"] = _agentId,
                        ["file"] = new Dictionary<string, object> { ["media_id"] = mediaId }
                    };
                    var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
                    await PostApiAsync("/cgi-bin/message/send", json, ct);
                    break;
                }

            case MediaMarkerKind.AudioUrl:
            case MediaMarkerKind.VideoUrl:
                _logger.LogDebug("企业微信暂不支持通过 REST API 直接发送音频/视频。");
                break;
        }
    }

    /// <summary>POST 请求到企业微信 REST API</summary>
    private async Task PostApiAsync(string path, string json, CancellationToken ct)
    {
        var url = $"{WeComApiBase}{path}?access_token={_accessToken}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("企业微信 API 调用失败：{Status} {Body}", response.StatusCode, errBody);
        }
        else
        {
            _logger.LogInformation("企业微信 API 调用成功：{Path}", path);
        }
    }

    // ════════════════════════════ 心跳 ════════════════════════════

    /// <summary>发送 ping 心跳帧</summary>
    private async Task SendPingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var pingMsg = new WeComWsRequest
        {
            Cmd = "aibot_ping",
            Headers = new WeComWsRequestHeaders { ReqId = Guid.NewGuid().ToString("N") }
        };

        var json = JsonSerializer.Serialize(pingMsg, WeComJsonContext.Default.WeComWsRequest);
        await SendWsTextDirectAsync(ws, json, ct);
    }

    // ════════════════════════════ 媒体上传 ════════════════════════════

    /// <summary>上传媒体文件到企业微信，返回 media_id</summary>
    private async Task<string?> UploadMediaAsync(byte[] data, string mediaType, CancellationToken ct)
    {
        var url = $"{WeComApiBase}/cgi-bin/media/upload?access_token={_accessToken}&type={mediaType}";

        using var content = new MultipartFormDataContent();
        var ext = mediaType == "image" ? ".jpg" : ".bin";
        var name = mediaType == "image" ? "image" : "file";
        content.Add(new ByteArrayContent(data), name, Guid.NewGuid().ToString("N") + ext);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("media_id", out var mediaId))
            return mediaId.GetString();

        _logger.LogWarning("企业微信媒体上传失败：{Body}", body);
        return null;
    }

    /// <summary>获取媒体文件字节数组</summary>
    private async Task<byte[]?> FetchMediaBytesAsync(MediaMarker marker, CancellationToken ct)
    {
        try
        {
            return marker.Kind is MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath
                ? await File.ReadAllBytesAsync(marker.Value, ct)
                : await _http.GetByteArrayAsync(marker.Value, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取媒体文件失败：{Source}", marker.Value);
            return null;
        }
    }

    // ════════════════════════════ REST API 鉴权 ════════════════════════════

    /// <summary>
    /// 获取/刷新 access_token。
    /// GET /cgi-bin/gettoken?corpid={corpid}&corpsecret={corpsecret}
    /// 返回 {"access_token":"...","expires_in":7200}
    /// Token 过期前 10 分钟自动刷新。
    /// </summary>
    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-10))
            return;

        var url = $"{WeComApiBase}/cgi-bin/gettoken?corpid={Uri.EscapeDataString(_corpId!)}&corpsecret={Uri.EscapeDataString(_corpSecret!)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errcode", out var errCode) && errCode.GetInt32() != 0)
        {
            var errMsg = GetString(root, "errmsg") ?? "unknown";
            throw new InvalidOperationException($"企业微信 access_token 获取失败：{errCode.GetInt32()} {errMsg}");
        }

        _accessToken = root.TryGetProperty("access_token", out var tokenProp)
            ? tokenProp.GetString()
            : throw new InvalidOperationException($"企业微信 access_token 响应中缺少 access_token：{body}");

        var expireSec = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 7200;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expireSec);

        _logger.LogInformation("企业微信 Access Token 已刷新（{Sec} 秒后过期）。", expireSec);
    }

    // ════════════════════════════ WebSocket 发送 ════════════════════════════

    /// <summary>通过 WebSocket 发送文本帧（线程安全，使用当前活跃连接）</summary>
    private async Task SendWsTextAsync(string text, CancellationToken ct)
    {
        var ws = _activeWs;
        if (ws is null || ws.State != WebSocketState.Open)
            return;

        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket 发送失败（连接可能已断开）。");
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    /// <summary>通过 WebSocket 发送文本帧（指定 ws 实例，用于消息循环内部调用）</summary>
    private async Task SendWsTextDirectAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    // ════════════════════════════ 辅助方法 ════════════════════════════

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;

    private static string? GetReqId(JsonElement root)
    {
        if (root.TryGetProperty("headers", out var headers) &&
            headers.ValueKind == JsonValueKind.Object)
            return GetString(headers, "req_id");
        return null;
    }

    private static bool IsUserAllowed(string userId, WeComChannelConfig cfg)
    {
        if (cfg.AllowedFromUserIds.Length > 0)
            return Array.Exists(cfg.AllowedFromUserIds,
                id => string.Equals(id, userId, StringComparison.Ordinal));
        return true;
    }

    private static bool IsGroupAllowed(string? groupId, WeComChannelConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return false;
        if (cfg.AllowedGroupIds.Length > 0)
            return Array.Exists(cfg.AllowedGroupIds,
                id => string.Equals(id, groupId, StringComparison.Ordinal));
        return true;
    }

    /// <summary>按 UTF-8 最大字节数截断文本</summary>
    private static string TruncateToMaxUtf8Bytes(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes)
            return s;

        var truncated = new byte[maxBytes];
        Array.Copy(bytes, truncated, maxBytes);
        return Encoding.UTF8.GetString(truncated);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop; }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
        _wsSendLock.Dispose();
        _restartLock.Dispose();
    }
}

// ════════════════════════════ JSON 序列化（AOT 安全） ════════════════════════════

/// <summary>企业微信 WebSocket 通用请求帧</summary>
public sealed class WeComWsRequest
{
    [JsonPropertyName("cmd")]
    public string Cmd { get; set; } = "";

    [JsonPropertyName("headers")]
    public WeComWsRequestHeaders Headers { get; set; } = new();

    [JsonPropertyName("body")]
    public object? Body { get; set; }
}

public sealed class WeComWsRequestHeaders
{
    [JsonPropertyName("req_id")]
    public string ReqId { get; set; } = "";
}

/// <summary>aibot_subscribe 鉴权请求的 body</summary>
public sealed class WeComSubscribeBody
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
}

/// <summary>aibot_respond_msg 回复消息的 body</summary>
public sealed class WeComRespondBody
{
    [JsonPropertyName("msgid")]
    public string MsgId { get; set; } = "";

    [JsonPropertyName("msgtype")]
    public string MsgType { get; set; } = "message";

    [JsonPropertyName("stream")]
    public WeComStreamInfo Stream { get; set; } = new();
}

/// <summary>流式回复信息（finish=true 表示结束）</summary>
public sealed class WeComStreamInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("finish")]
    public bool Finish { get; set; } = true;

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>企业微信 REST API 通用响应（获取 token 等）</summary>
public sealed class WeComTokenResponse
{
    [JsonPropertyName("errcode")]
    public int ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

/// <summary>企业微信媒体上传响应</summary>
public sealed class WeComMediaUploadResponse
{
    [JsonPropertyName("errcode")]
    public int ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("media_id")]
    public string? MediaId { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

[JsonSerializable(typeof(WeComWsRequest))]
[JsonSerializable(typeof(WeComTokenResponse))]
[JsonSerializable(typeof(WeComMediaUploadResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class WeComJsonContext : JsonSerializerContext;
