namespace OpenClaw.Core.Models;

// kingcrab-specific channel configuration types.
// Restored from pre-content-sync state (GatewayConfig.cs.bak) because upstream
// openclaw.net does not ship Feishu / DingTalk / WeCom channels but kingcrab
// does (see OpenClaw.Channels/{Feishu,DingTalk,WeCom}Channel.cs).

/// <summary>
/// Configuration for the Feishu (Lark) channel.
/// Uses WebSocket long connection; no public webhook endpoint needed — suitable for intranet/sandbox deployments.
/// Supports config hot-reload: change values in appsettings and the channel reconnects automatically.
/// </summary>
public sealed class FeishuChannelConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Feishu App ID (direct value). Takes precedence over AppIdRef when set.</summary>
    public string? AppId { get; set; }

    /// <summary>Secret reference for App ID (e.g. "env:FEISHU_APP_ID"). Used when AppId is null.</summary>
    public string AppIdRef { get; set; } = "env:FEISHU_APP_ID";

    /// <summary>Feishu App Secret (direct value). Avoid in production; prefer AppSecretRef.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Secret reference for App Secret (e.g. "env:FEISHU_APP_SECRET").</summary>
    public string AppSecretRef { get; set; } = "env:FEISHU_APP_SECRET";

    /// <summary>Group chat policy: "open" allows all groups, "allowlist" restricts to AllowedGroupIds, "disabled" drops group messages.</summary>
    public string GroupPolicy { get; set; } = "open"; // open, allowlist, disabled

    /// <summary>Allowed sender open_ids. Empty = allow all (subject to DmPolicy/GroupPolicy).</summary>
    public string[] AllowedFromUserIds { get; set; } = [];

    /// <summary>Allowed group chat_ids (oc_xxx). Only used when GroupPolicy is "allowlist".</summary>
    public string[] AllowedGroupIds { get; set; } = [];

    public int MaxInboundChars { get; set; } = 4096;

    /// <summary>
    /// When true, the bot only responds to group messages where it is explicitly @mentioned.
    /// Recommended when multiple bots share the same group.
    /// Default is false (respond to all group messages allowed by GroupPolicy).
    /// </summary>
    public bool RequireMentionInGroup { get; set; } = false;

    /// <summary>
    /// When true, inbound media file keys are included as feishu-resource:// URLs in MediaUrl.
    /// The pipeline can pass these to tools that understand the scheme.
    /// </summary>
    public bool ExposeInboundMediaUrls { get; set; } = true;
}

public sealed class DingTalkChannelConfig
{
    public bool Enabled { get; set; } = false;

    public string? AppId { get; set; }
    public string AppIdRef { get; set; } = "env:DINGTALK_APP_ID";

    public string? AppKey { get; set; }
    public string AppKeyRef { get; set; } = "env:DINGTALK_APP_KEY";

    public string? AppSecret { get; set; }
    public string AppSecretRef { get; set; } = "env:DINGTALK_APP_SECRET";

    /// <summary>机器人 RobotCode，默认与 AppKey 相同，发消息时必填</summary>
    public string? RobotCode { get; set; }
    public string RobotCodeRef { get; set; } = "env:DINGTALK_ROBOT_CODE";

    public string GroupPolicy { get; set; } = "open";
    public string[] AllowedFromUserIds { get; set; } = [];
    public string[] AllowedGroupIds { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public bool RequireMentionInGroup { get; set; } = true;
    public bool ExposeInboundMediaUrls { get; set; } = true;
    public int StreamPollIntervalMs { get; set; } = 500;
}

/// <summary>
/// 企业微信（WeCom）智能机器人通道配置。
/// 使用 WebSocket 长连接模式接收消息，REST API 发送消息。
/// </summary>
public sealed class WeComChannelConfig
{
    public bool Enabled { get; set; } = true;

    // ── WebSocket 长连接凭证（智能机器人） ──
    /// <summary>智能机器人 BotId，格式：aib-xxxxx</summary>
    public string? BotId { get; set; }
    public string BotIdRef { get; set; } = "env:WECOM_BOT_ID";

    /// <summary>智能机器人长连接专用 Secret</summary>
    public string? BotSecret { get; set; }
    public string BotSecretRef { get; set; } = "env:WECOM_BOT_SECRET";

    // ── REST API 凭证（自建应用，用于主动发送消息和媒体上传） ──
    /// <summary>企业 CorpID</summary>
    public string? CorpId { get; set; }
    public string CorpIdRef { get; set; } = "env:WECOM_CORP_ID";

    /// <summary>自建应用 AgentId</summary>
    public int AgentId { get; set; }
    public string AgentIdRef { get; set; } = "env:WECOM_AGENT_ID";

    /// <summary>自建应用 Secret，用于换取 access_token</summary>
    public string? CorpSecret { get; set; }
    public string CorpSecretRef { get; set; } = "env:WECOM_CORP_SECRET";

    // ── 通用配置 ──
    /// <summary>群聊策略：open（全部允许）、allowlist（白名单）、disabled（丢弃群消息）</summary>
    public string GroupPolicy { get; set; } = "open";

    /// <summary>允许的发信人 userid 列表，空数组表示允许全部</summary>
    public string[] AllowedFromUserIds { get; set; } = [];

    /// <summary>允许的群聊 chatid 列表，仅在 GroupPolicy=allowlist 时生效</summary>
    public string[] AllowedGroupIds { get; set; } = [];

    /// <summary>入站消息最大字符数</summary>
    public int MaxInboundChars { get; set; } = 4096;

    /// <summary>群聊中是否需要 @机器人 才响应</summary>
    public bool RequireMentionInGroup { get; set; } = true;
}
