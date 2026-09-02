# 项目沿革与 agent_id / session_id 维度说明

> 目的：把本仓库（工作目录 `kingcrab` / 项目名 `OpenClaw.NET`）的来源脉络、与上游开源项目的关系，以及 token 用量统计里 `agent_id` 与 `session_id` 的分层口径，写成一份长期可参考的内部文档，避免下次新会话再翻 README + CHANGELOG 拼凑。

---

## 1. 项目是什么

| 项 | 值 | 证据 |
| --- | --- | --- |
| 仓库工作目录 | `kingcrab` | `E:\Documents\CODES\ai4c_Projects\kingcrab` |
| 项目名 | **OpenClaw.NET** | `README.md:5` 标题 |
| 解决方案 | `OpenClaw.Net.slnx`（19 个 `OpenClaw.*` 项目 + `Kingcrab.AppHost` / `Kingcrab.ServiceDefaults`） | `CHANGELOG.md:8` |
| 目标框架 | `net10.0`，`LangVersion=14`，`TrimMode=link`（NativeAOT 友好） | `Directory.Build.props` |
| 许可 | MIT | `Directory.Build.props:18` `<PackageLicenseExpression>MIT</PackageLicenseExpression>` |
| Authors / Company | `clawdotnet` | `Directory.Build.props:16-17` |
| 仓库 URL | `https://github.com/clawdotnet/openclaw.net` | `Directory.Build.props:19` |
| 自述定位 | "Independent .NET implementation of the OpenClaw agent runtime and gateway" | `README.md:9` |

### 1.1 架构分层

```
Kingcrab.AppHost + Kingcrab.ServiceDefaults   ← .NET Aspire 编排
        │
        ▼
OpenClaw.Gateway          ← HTTP / WebSocket / Webhook / 内置 /chat
OpenClaw.Agent            ← Agent 运行时（MAF-only 编排）
OpenClaw.Core             ← Session / 记忆 / 配置 / 安全
OpenClaw.Channels         ← Discord / Teams / Telegram / Twilio / WhatsApp(Baileys) / WeCom / Feishu / DingTalk
OpenClaw.PluginKit
OpenClaw.SkillKit(.Abstractions)   ← JS/TS 插件桥接 + SKILL.md 包
OpenClaw.TokenHubSink
OpenClaw.TokenCollector   ← token 用量 → TokenHub.Collector → Kafka → Doris
OpenClaw.Companion        ← Avalonia 桌面客户端
OpenClaw.Tui              ← TUI 客户端
OpenClaw.Dashboard        ← Blazor WASM 运营看板
OpenClaw.Client           ← 共享 HTTP/MCP SDK
OpenClaw.Cli              ← 命令行客户端
OpenClaw.Payments.*       ← Stripe Link 支付
OpenClaw.Plugins.*        ← EmploymentCoachWorkflow / Mempalace / Payment 业务插件
OpenClawNet.Sandbox.OpenSandbox  ← 可选 OpenSandbox 沙箱（-p:OpenClawEnableOpenSandbox=true）
```

### 1.2 两条运行时轨道

通过 `OpenClaw:Runtime:Mode` 切换：

- `aot`：trim-safe、低内存，`auto` 模式下默认。覆盖主流 tool/skill 兼容面。
- `jit`：扩展 JS/TS 插件兼容面，支持 `registerChannel()` / `registerCommand()` / `registerProvider()` / `api.on(...)` 与原生动态插件。需要 dynamic code 支持。
- `auto`：根据 dynamic code 可用性在 `aot` / `jit` 之间二选一。

完整矩阵见 `COMPATIBILITY.md`。

---

## 2. 项目来源 / 演进时间线

```
openclaw/openclaw   ← 上游架构灵感源（MIT，openclaw.ai）
        │
        ▼
clawdotnet/openclaw.net   ← 上游 .NET 实现（openclaw.net 项目本身）
        │   2026-03-05：openclaw.net 整项迁入 kingcrab（结构性合并）
        ▼
kingcrab（本工作目录）   ← 在 openclaw.net 基础上做 kingcrab 特有的调整
```

### 2.1 上游 A：`openclaw/openclaw`

- 仓库地址：**https://github.com/openclaw/openclaw**
- 自述："Your own personal AI assistant. Any OS. Any Platform. The lobster way. 🦞"
- 许可：MIT
- 与本仓库的关系：架构灵感源。`README.md:11` 明确写明："This project is not affiliated with, endorsed by, or associated with [OpenClaw](https://github.com/openclaw/openclaw). It is an independent implementation inspired by their work."
- 桥接对象：本仓库通过 Node.js 进程间通信桥接上游的 JS/TS 插件（`api.registerTool` / `registerChannel` / `registerCommand` / `registerProvider` / `api.on(...)`），术语、`SKILL.md` 包格式、`openclaw.plugin.json` manifest 都与上游对齐。

### 2.2 上游 B：`clawdotnet/openclaw.net`

- 仓库地址：**https://github.com/clawdotnet/openclaw.net**（`Directory.Build.props:19` 的 `RepositoryUrl` / `PackageProjectUrl`）
- 性质：上游 `openclaw/openclaw` 的 .NET 实现，由 `clawdotnet` 维护
- 与本仓库的关系：`CHANGELOG.md:7` —— "## [Unreleased] - 2026-03-05 / openclaw.net 迁入 kingcrab（结构性合并）"
  - 19 个 OpenClaw.* 项目整项迁入
  - 328 个 `only-in-openclaw` 文件按 diff 合并（详细差异见 `docs/migration/diff-*.md`）
  - 迁移脚本 `scripts/migration/content-sync.ps1` / `scripts/migration/refresh-diff.ps1` 中的 `$UpstreamRoot = 'E:\GitHub\openclaw.net\src'`，证明 kingcrab 本地是 `openclaw.net` 的下游分支/工作副本

### 2.3 kingcrab 在合并后的特有调整

- **MAF-only 编排**：原 `AgentRuntime` / `NativeAgentRuntimeFactory` / `RuntimeInitializationExtensions.MafConfigNotices` 已迁出（`docs/migration/migration-summary.md:8`），`OpenClaw.Agent` 现在只用 Microsoft Agent Framework 编排
- **TickerQ → NCrontab**：`CronSchedulerStartupService` / `CronSchedulerTickerFunction` 全部删除，统一用 `NCrontab.CrontabSchedule.Parse`（`docs/migration/migration-summary.md:54`）
- **业务渠道扩展**：新增 Feishu / DingTalk / WeCom 渠道（`KingcrabChannelConfigs.cs`）
- **业务插件**：`OpenClaw.Plugins.EmploymentCoachWorkflow`（就业教练工作流）
- **观测链 `OpenClaw.TokenHubSink` + `OpenClaw.TokenCollector`**：把每次 LLM 调用的 token 用量推送到 `TokenHub.Collector → Kafka → Doris`，用于按数字员工聚合成本
- **OpenSandbox 沙箱（可选）**：`OpenClawNet.Sandbox.OpenSandbox`，通过 `-p:OpenClawEnableOpenSandbox=true` 启用；底层是 [AIDotNet/OpenSandbox](https://github.com/AIDotNet/OpenSandbox)，用于把 `shell` / `code_exec` / `browser` 高危工具从 gateway host 隔离开
- **迁移期剔除项**：
  - B 组：`OpenClaw.MicrosoftAgentFrameworkAdapter`（已并入 `OpenClaw.Agent`）、`OpenClaw.Providers.MicrosoftExtensionsAI`（MEAI Provider）、`OpenClaw.SemanticKernelAdapter`（SK 适配器）
  - C 组：`OpenClaw.Embeddings.Onnx`（源代码缺失）、`whatsapp-whatsmeow-worker/`（保留 Baileys Worker）、`samples/*`
  - `OpenClaw.Plugins.Mempalace` 整项移除（依赖的 `IMemoryNoteCatalog` / `NativeDynamicMemoryProviderContext` 在 kingcrab 不存在）

---

## 3. token 用量统计里的两个独立维度：`agent_id` 与 `session_id`

**核心结论：除了 `agent_id`（数字员工）之外，存在一个独立的一级字段 `session_id`，用来标识"一次连续对话/任务"。它们在事件契约里是平级的 required 字段，且 token 累计口径也分两层。**

### 3.1 线协议：`SessionTokenUsageEvent`

推送到 TokenHub → Kafka → Doris 的事件契约，定义在 `src/OpenClaw.TokenHubSink/Observability/TokenUsageEvents.cs:14-64`：

```csharp
public sealed record SessionTokenUsageEvent
{
    [JsonPropertyName("event_id")]    public string EventId { get; init; }
    [JsonPropertyName("event_time")]  public DateTimeOffset EventTime { get; init; }

    /// <summary>Digital employee id. Defaults to the session's SenderId unless a fixed id is configured.</summary>
    [JsonPropertyName("agent_id")]    public required string AgentId { get; init; }

    [JsonPropertyName("session_id")]  public required string SessionId { get; init; }

    [JsonPropertyName("channel_id")]  public string ChannelId { get; init; } = "";
    [JsonPropertyName("provider_id")] public string ProviderId { get; init; } = "";
    [JsonPropertyName("model_id")]    public string ModelId { get; init; } = "";

    // 本次 LLM 调用的增量（下游可直接 SUM）
    [JsonPropertyName("input_tokens")]         public long InputTokens { get; init; }
    [JsonPropertyName("output_tokens")]        public long OutputTokens { get; init; }
    [JsonPropertyName("cache_read_tokens")]    public long CacheReadTokens { get; init; }
    [JsonPropertyName("total_tokens")]         public long TotalTokens { get; init; }

    // ★ 本 session 截至当前的累计快照（仅做对账，禁止 SUM）
    [JsonPropertyName("session_total_input_tokens")]      public long SessionTotalInputTokens { get; init; }
    [JsonPropertyName("session_total_output_tokens")]     public long SessionTotalOutputTokens { get; init; }
    [JsonPropertyName("session_total_cache_read_tokens")] public long SessionTotalCacheReadTokens { get; init; }
    [JsonPropertyName("session_total_tokens")]            public long SessionTotalTokens { get; init; }
}
```

要点：
- snake_case 的 JSON 字段名是与 TokenHub.Core 的 `SessionTokenUsageEvent` 和 Doris Routine Load 的 jsonpath 的**字节级契约**，不能改名字（见同文件 XML 注释）。
- `agent_id` 与 `session_id` 都是 `required`，平级。

### 3.2 映射来源：`TurnTokenUsageRecord`

事件契约从 `TurnTokenUsageRecord` 映射而来（`src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs`）：

```csharp
public sealed record TurnTokenUsageRecord
{
    public string? CorrelationId { get; init; }
    public required string SessionId { get; init; }   // 本轮所在 session
    public required string ChannelId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
    public bool IsEstimated { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    // In-process session snapshot (does NOT widen the cross-process wire contract).
    // Carried on the record so a singleton ITurnTokenUsageObserver — which only
    // receives the record — can map the TokenHub event without holding a Session reference.
    public string SenderId { get; init; } = "";              // agentId fallback when no fixed id
    public long SessionTotalInputTokens { get; init; }
    public long SessionTotalOutputTokens { get; init; }
    public long SessionTotalCacheReadTokens { get; init; }
    public long SessionTotalTokens { get; init; }
}
```

注意：`SessionTotal*` 是 **session 维度的滚动累计**（不是 agent 维度）。

### 3.3 映射器：`TokenUsageEventMapper`

`src/OpenClaw.Agent/TokenUsageEventMapper.cs:16-36`：

```csharp
public static SessionTokenUsageEvent Create(TurnTokenUsageRecord record, string? fixedAgentId)
{
    var agentId = string.IsNullOrEmpty(fixedAgentId) ? record.SenderId : fixedAgentId;
    return new SessionTokenUsageEvent
    {
        AgentId   = agentId,                  // 数字员工：优先 fixedAgentId，回退到 SenderId
        SessionId = record.SessionId,          // 一次连续会话：直接来自 TurnTokenUsageRecord
        ChannelId = record.ChannelId,
        ProviderId = record.ProviderId,
        ModelId    = record.ModelId,
        InputTokens          = record.InputTokens,
        OutputTokens         = record.OutputTokens,
        CacheReadTokens      = record.CacheReadTokens,
        TotalTokens          = record.InputTokens + record.OutputTokens,
        SessionTotalInputTokens      = record.SessionTotalInputTokens,
        SessionTotalOutputTokens     = record.SessionTotalOutputTokens,
        SessionTotalCacheReadTokens  = record.SessionTotalCacheReadTokens,
        SessionTotalTokens           = record.SessionTotalTokens,
    };
}
```

### 3.4 Session 生命周期：`SessionManager`

`src/OpenClaw.Core/Sessions/SessionManager.cs`：

```csharp
/// Get or create a session for the given channel+sender pair.
/// Session key is deterministic: channelId:senderId
public async ValueTask<Session> GetOrCreateAsync(string channelId, string senderId, CancellationToken ct)

/// Get or create a session for an explicit session id. Useful for cron jobs and webhooks
/// that want stable, named sessions independent of channel+sender.
public async ValueTask<Session> GetOrCreateByIdAsync(string sessionId, string channelId, string senderId, CancellationToken ct)
```

`Session` 模型（`src/OpenClaw.Core/Models/Session.cs`）还带 `ExternalSessionId`（行 150）、`ParentSessionId`（行 236）、`SessionId`（行 269），支持父子会话链路。

### 3.5 下游聚合口径

| 维度 | 取值 | 说明 |
| --- | --- | --- |
| **数字员工累计成本** | `SUM(input_tokens)` / `SUM(output_tokens)` 按 `agent_id` 分组 | 用每次事件的增量字段（`input_tokens` / `output_tokens` / `cache_read_tokens`） |
| **单次任务累计** | 按 `session_id` 取最后一条事件的 `session_total_*` | 滚动累计快照，仅做对账，**不能 SUM**（重复加会爆） |
| **Provider / Model 用量** | `SUM(input_tokens)` 按 `agent_id` + `provider_id` + `model_id` 分组 | 跨 session 累加 |

`conversation_id` 在仓库里出现 22 次 / 10 个文件，但它不是主维度 —— 它出现在 `GatewayConfig.AllowedConversationIds`（`src/OpenClaw.Core/Models/GatewayConfig.cs:755`）和 `OpenAiEndpoints.StableSessions.cs` 里，是 OpenAI-Compatible 路由白名单 / StableSessions 场景下的旁路别名，**不是** 主键维度。

---

## 4. 一句话总结

- **项目身份**：`OpenClaw.NET`（目录 `kingcrab`），MIT，.NET 10 + Aspire + NativeAOT 友好的 OpenClaw 独立 .NET 实现；MAF 编排、TokenHub → Kafka → Doris 成本链、可选 OpenSandbox 沙箱。
- **上游**：架构灵感来自 [openclaw/openclaw](https://github.com/openclaw/openclaw)；直接祖先是 [clawdotnet/openclaw.net](https://github.com/clawdotnet/openclaw.net)，已于 2026-03-05 整项迁入 kingcrab。
- **维度分层**：token 用量事件契约里 **`agent_id`**（数字员工，跨多次会话的长期身份）与 **`session_id`**（一次连续对话/任务）是两个**独立**的平级 required 字段；累计增量按 `agent_id` SUM，单次任务累计按 `session_id` 取 `session_total_*` 快照。