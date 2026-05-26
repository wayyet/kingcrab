using System.Buffers;
using System.Collections.Concurrent;
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

    // ── 消息去重：key=msgid, value=过期时间(Unix ms) ──
    private readonly ConcurrentDictionary<string, long> _dedup = new(StringComparer.Ordinal);
    private const long DedupTtlMs = 5L * 60 * 1_000; // 5 分钟 TTL
    private const int DedupMaxSize = 2_000; // 最多 2000 条

    // ── 媒体下载临时目录 ──
    private static readonly string MediaTempDir = Path.Combine(
        Path.GetTempPath(), "openclaw_wecom");

    // ── 入站消息上下文缓存（用于快速 WebSocket 回复） ──
    // key: chatid 或 userid, value: 最近一次消息的上下文信息
    private readonly ConcurrentDictionary<string, InboundMsgContext> _inboundContexts = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存最近一次入站消息的上下文，用于 WebSocket 快速回复。
    /// </summary>
    /// <param name="ReqId">消息回调用 req_id，回复时需透传</param>
    private sealed record InboundMsgContext(string ReqId, DateTimeOffset ReceivedAt);

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
        await RestartAsync(ct);
    }

    // ════════════════════════════ 生命周期 ════════════════════════════

    public async Task StartAsync(CancellationToken ct)
    {
        _appLifetime = ct;

        var cfg = GetEffectiveConfig();
        if (!cfg.Enabled)
            return;

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
                    catch (Exception) { }
                }
                _cts.Dispose();
                _cts = null;
                _receiveLoop = null;
            }

            var cfg = GetEffectiveConfig();
            if (!cfg.Enabled)
                return;

            ResolveCredentials(cfg);
            if (!ValidateWsCredentials())
                return;

            // 清除缓存状态
            _accessToken = null;
            _tokenExpiry = DateTimeOffset.MinValue;
            _inboundContexts.Clear();
            _dedup.Clear();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime);
            _receiveLoop = RunWsLoopAsync(_cts.Token);
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
    /// </summary>
    private async Task SendSubscribeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString("N");
        var json = BuildWsMessage("aibot_subscribe", reqId,
            $"\"bot_id\":{JsonString(_botId!)},\"secret\":{JsonString(_botSecret!)}");

        await SendWsTextDirectAsync(ws, json, ct);
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
                continue;

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            try
            {
                await HandleWsFrameAsync(json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "企业微信 WebSocket 帧处理异常：{Json}", json);
            }
        }
    }

    /// <summary>
    /// 分发 WebSocket 帧：按 cmd 字段路由到对应的处理函数。
    /// 注意：企业微信服务器的响应帧（subscribe 结果、心跳响应等）不带 cmd 字段，
    /// 格式为 {headers:{req_id}, errcode:0, errmsg:"ok"}。
    /// </summary>
    private async Task HandleWsFrameAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cmd = GetString(root, "cmd");
        var reqId = GetReqId(root);

        // 无 cmd 字段 → 服务器响应帧（subscribe / ping / upload 等）
        if (cmd is null)
        {
            var errCode = root.TryGetProperty("errcode", out var ec) ? ec.GetInt32() : -1;
            if (errCode != 0)
            {
                var errMsg = GetString(root, "errmsg");
                _logger.LogError("企业微信 响应失败 req_id={ReqId} errcode={ErrCode} errmsg={ErrMsg}", reqId, errCode, errMsg);
            }
            return;
        }

        switch (cmd)
        {
            // ── 消息回调 ──
            case "aibot_msg_callback":
                await HandleMsgCallbackAsync(root, reqId, ct);
                return;

            // ── 事件回调 ──
            case "aibot_event_callback":
                HandleEventCallback(root);
                return;

            default:
                return;
        }
    }

    // ════════════════════════════ 消息处理 ════════════════════════════

    /// <summary>
    /// 处理 aibot_msg_callback：解析消息体 → 过滤 → 构建 InboundMessage → 触发 OnMessageReceived。
    /// </summary>
    private async Task HandleMsgCallbackAsync(JsonElement root, string? reqId, CancellationToken ct)
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
        string? senderName = null;
        if (body.TryGetProperty("from", out var fromProp))
        {
            senderId = GetString(fromProp, "userid");
            senderName = GetString(fromProp, "name") ?? GetString(fromProp, "username");
        }

        // 提取消息文本
        var text = ReadWeComText(body);

        // ── 消息去重 ──
        if (!string.IsNullOrWhiteSpace(msgId) && !TryClaimDedup(msgId))
            return;

        // ── 回复消息提取 ReplyToMessageId（quote 引用） ──
        string? replyToMessageId = null;
        if (body.TryGetProperty("quote", out var quote))
        {
            replyToMessageId = msgId; // 企业微信 quote 不返回原始 msgid，用当前 msgId 作为关联
        }

        // ── 媒体文件下载 ──
        var mediaText = await DownloadWeComMediaAsync(body, msgType, msgId, ct);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(mediaText))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(senderId))
            return;

        var isGroup = string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase);

        // ── 群聊策略过滤 ──
        if (isGroup)
        {
            if (string.Equals(cfg.GroupPolicy, "disabled", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(cfg.GroupPolicy, "allowlist", StringComparison.OrdinalIgnoreCase) &&
                !IsGroupAllowed(chatId, cfg))
                return;
        }

        // ── 发信人白名单过滤 ──
        if (!IsUserAllowed(senderId, cfg))
            return;

        // ── @提及 提取 ──
        string[]? mentionedIds = null;
        var isBotMentioned = false;
        if (body.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.Object)
        {
            var content = GetString(textProp, "content") ?? "";
            // 企业微信群聊 @机器人 内容格式为 "@BotName 消息内容"，提取 @ 提及的 userId
            if (content.Contains('@'))
            {
                isBotMentioned = true;
                // 尝试从 mentions 数组获取（如果企业微信提供了）
                if (textProp.TryGetProperty("mentions", out var mentionsArr) && mentionsArr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var m in mentionsArr.EnumerateArray())
                    {
                        var uid = GetString(m, "userid");
                        if (!string.IsNullOrWhiteSpace(uid))
                            list.Add(uid);
                    }
                    if (list.Count > 0)
                        mentionedIds = [.. list];
                }
            }
        }

        // ── 群聊 @提及 过滤 ──
        if (isGroup && cfg.RequireMentionInGroup && !isBotMentioned)
            return;

        // ── 合并文本和媒体标记 ──
        var finalText = text ?? "";
        if (!string.IsNullOrWhiteSpace(mediaText))
            finalText = string.IsNullOrWhiteSpace(finalText) ? mediaText : finalText + "\n" + mediaText;

        // ── 文本截断 ──
        if (finalText.Length > cfg.MaxInboundChars)
            finalText = finalText[..cfg.MaxInboundChars];

        // ── 缓存入站上下文（用于后续 WebSocket 快速回复） ──
        CacheInboundContext(msgId, reqId ?? "", chatId, senderId);

        // ── 构建 InboundMessage ──
        var inbound = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            SenderName = senderName,
            Text = finalText,
            MessageId = msgId,
            ReplyToMessageId = replyToMessageId,
            IsGroup = isGroup,
            GroupId = isGroup ? chatId : null,
            MentionedIds = mentionedIds,
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
    private void HandleEventCallback(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body))
            return;

        string? eventType = null;
        if (body.TryGetProperty("event", out var eventProp) &&
            eventProp.ValueKind == JsonValueKind.Object)
        {
            eventType = GetString(eventProp, "eventtype");
        }
        eventType ??= GetString(body, "eventtype");

        switch (eventType)
        {
            case "enter_chat":
            case "disconnected_event":
                break;
        }
    }

    /// <summary>缓存入站消息上下文，用于后续 WebSocket 快速回复（透传 reqId）</summary>
    private void CacheInboundContext(string? msgId, string reqId, string? chatId, string? senderId)
    {
        if (string.IsNullOrWhiteSpace(msgId) || string.IsNullOrWhiteSpace(reqId))
            return;

        var ctx = new InboundMsgContext(reqId, DateTimeOffset.UtcNow);

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

    // ════════════════════════════ 消息去重 ════════════════════════════

    /// <summary>消息去重：检查 msgid 是否已处理过，未处理则标记并返回 true。</summary>
    private bool TryClaimDedup(string msgId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_dedup.TryGetValue(msgId, out var expMs) && expMs > now)
            return false;

        _dedup[msgId] = now + DedupTtlMs;

        if (_dedup.Count > DedupMaxSize)
            EvictExpiredDedup(now);

        return true;
    }

    private void EvictExpiredDedup(long now)
    {
        foreach (var key in _dedup.Keys.ToList())
        {
            if (_dedup.TryGetValue(key, out var expMs) && expMs <= now)
                _dedup.TryRemove(key, out _);
        }
    }

    // ════════════════════════════ 媒体下载 ════════════════════════════

    /// <summary>
    /// 下载企业微信消息中的媒体文件（图片/文件/语音），保存到临时目录，
    /// 返回 [IMAGE_PATH:...] 或 [FILE_PATH:...] 标记。
    /// </summary>
    private async Task<string?> DownloadWeComMediaAsync(JsonElement body, string msgType, string? msgId, CancellationToken ct)
    {
        try
        {
            string? mediaId = null;
            var propName = msgType switch
            {
                "image" => "image",
                "file" => "file",
                "voice" => "voice",
                "video" => "video",
                _ => null
            };

            if (propName is not null &&
                body.TryGetProperty(propName, out var prop) &&
                prop.ValueKind == JsonValueKind.Object)
            {
                mediaId = GetString(prop, "media_id");
            }

            if (string.IsNullOrWhiteSpace(mediaId))
                return null;

            // 需要 REST API 凭证才能下载
            if (!HasApiCredentials())
                return BuildMediaMarker(body, msgType); // 回退到纯文本标记

            await RefreshAccessTokenAsync(ct);

            var url = $"{WeComApiBase}/cgi-bin/media/get?access_token={_accessToken}&media_id={Uri.EscapeDataString(mediaId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return BuildMediaMarker(body, msgType);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "application/pdf" => ".pdf",
                "audio/amr" => ".amr",
                "audio/mp3" => ".mp3",
                "video/mp4" => ".mp4",
                _ => msgType switch
                {
                    "image" => ".jpg",
                    "voice" => ".amr",
                    "video" => ".mp4",
                    _ => ".bin"
                }
            };

            Directory.CreateDirectory(MediaTempDir);
            var filePath = Path.Combine(MediaTempDir, $"{Guid.NewGuid():N}{ext}");
            await using var fs = File.Create(filePath);
            await response.Content.CopyToAsync(fs, ct);

            return msgType switch
            {
                "image" => $"[IMAGE_PATH:{filePath}]",
                "voice" => $"[VOICE_PATH:{filePath}]",
                "video" => $"[VIDEO_PATH:{filePath}]",
                _ => $"[FILE_PATH:{filePath}]"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载企业微信媒体失败 msgId={MsgId}。", msgId);
            return BuildMediaMarker(body, msgType); // 回退到文本标记
        }
    }

    /// <summary>
    /// 构建媒体标记（纯文本回退）。企业微信图片/文件/语音/视频消息会携带 media_id，
    /// 当无法下载时，将其转换为 [IMAGE:wecom:...] 等标记供 LLM 参考。
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
                    // 智能机器人主动发送应走 WebSocket aibot_send_msg。
                    var sentViaActiveWs = await TrySendTextViaActiveWsAsync(outbound.RecipientId, remaining, ct);
                    if (!sentViaActiveWs && !HasApiCredentials())
                    {
                        _logger.LogWarning("企业微信 REST API 凭证未配置，无法主动发送消息到 {RecipientId}。", outbound.RecipientId);
                    }
                    else if (!sentViaActiveWs)
                    {
                        // 最后才回退到自建应用 REST API；该消息会以自建应用身份发送，不是智能机器人身份。
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
                if (!sentViaWs)
                    sentViaWs = await TrySendTextViaActiveWsAsync(outbound.RecipientId, outbound.Text, ct);
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

        var streamId = Guid.NewGuid().ToString("N");
        // headers.req_id 必须透传消息回调中的原始 req_id
        // msgtype 必须为 "stream"
        var body = $"\"msgtype\":\"stream\"," +
                   $"\"stream\":{{\"id\":{JsonString(streamId)},\"finish\":true,\"content\":{JsonString(text)}}}";
        var json = BuildWsMessage("aibot_respond_msg", ctx.ReqId, body);

        return await SendWsTextAsync(json, ct);
    }

    /// <summary>通过智能机器人 WebSocket 主动发送 Markdown 消息。</summary>
    private async Task<bool> TrySendTextViaActiveWsAsync(string recipientId, string text, CancellationToken ct)
    {
        var reqId = "aibot_send_msg_" + Guid.NewGuid().ToString("N");
        var body = $"\"chatid\":{JsonString(recipientId)}," +
                   $"\"msgtype\":\"markdown\"," +
                   $"\"markdown\":{{\"content\":{JsonString(TruncateToMaxUtf8Bytes(text, 20480))}}}";
        var json = BuildWsMessage("aibot_send_msg", reqId, body);

        return await SendWsTextAsync(json, ct);
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
    }

    // ════════════════════════════ 心跳 ════════════════════════════

    /// <summary>发送 ping 心跳帧</summary>
    private async Task SendPingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString("N");
        var json = BuildWsMessage("ping", reqId, null);
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
    }

    // ════════════════════════════ WebSocket 发送 ════════════════════════════

    /// <summary>
    /// 构建企业微信 WebSocket 消息帧。
    /// 格式：{"cmd":"...","headers":{"req_id":"..."},"body":{...}}
    /// bodyJson 为 body 对象的 JSON 片段（不含外层大括号），为 null 时省略 body 字段。
    /// </summary>
    private static string BuildWsMessage(string cmd, string reqId, string? bodyJson)
    {
        if (bodyJson is null)
            return $"{{\"cmd\":{JsonString(cmd)},\"headers\":{{\"req_id\":{JsonString(reqId)}}}}}";
        return $"{{\"cmd\":{JsonString(cmd)},\"headers\":{{\"req_id\":{JsonString(reqId)}}},\"body\":{{{bodyJson}}}}}";
    }

    /// <summary>序列化 JSON 字符串值，包含外层引号。</summary>
    private static string JsonString(string value)
        => $"\"{JsonEncodedText.Encode(value)}\"";

    /// <summary>通过 WebSocket 发送文本帧（线程安全，使用当前活跃连接）。返回 true 表示发送成功。</summary>
    private async Task<bool> SendWsTextAsync(string text, CancellationToken ct)
    {
        var ws = _activeWs;
        if (ws is null || ws.State != WebSocketState.Open)
            return false;

        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
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

[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class WeComJsonContext : JsonSerializerContext;
