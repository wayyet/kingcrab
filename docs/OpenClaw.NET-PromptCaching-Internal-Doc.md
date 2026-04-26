# OpenClaw.NET 提示词缓存子系统 —— 内部技术文档

> **版本**: v1.0   
> **定位**: 感知提供商的优化层，位于 Agent 运行时与上游 LLM 提供商之间  
> **核心约束**: 零侵入 —— 不替换、不分流现有 `ILlmExecutionService` 执行流水线

---

## 1. 设计哲学：为什么是"增强"而非"替换"

### 1.1 核心命题

在典型 Agent 会话中，提示词的大部分内容在多轮对话中保持稳定：
- 基础系统提示词（System Prompt）
- 工具声明（Tool Declarations）
- 技能提示词内容（Skill Prompts）
- 稳定的工作区提示词文件

当上游提供商支持提示词缓存时，OpenClaw 可以附加缓存提示，使提供商能够跳过对这些稳定 Token 的重新处理。其结果是：
- **降低延迟** —— 提供商无需对缓存前缀重新计算注意力
- **降低成本** —— 许多提供商以折扣价对缓存 Token 计费

### 1.2 关键设计决策：零侵入

```
┌─────────────────────────────────────────────────────────────┐
│                        Agent Runtime                         │
│                   (AgentRuntime.cs)                          │
│                      ↓ 零侵入 ↓                              │
│              ILlmExecutionService 接口不变                    │
│                      ↓ 增强 ↓                                │
│           ┌──────────────────────┐                          │
│           │ PromptCacheCoordinator │ ← 仅修改 ChatOptions   │
│           │   (请求塑形层)         │   .AdditionalProperties │
│           └──────────────────────┘                          │
│                      ↓ 标准化 ↓                              │
│         PromptCacheUsageExtractor                          │
│         cacheRead / cacheWrite 统一模型                     │
│                      ↓ 上报 ↓                                │
│         ProviderUsageTracker / RuntimeMetrics               │
└─────────────────────────────────────────────────────────────┘
```

**设计原则**:
1. **不引入提供商特定运行时分支** —— Agent 运行时中不存在提供商特定代码
2. **仅修改请求塑形** —— 通过 `ChatOptions.AdditionalProperties` 字典注入缓存元数据
3. **统一规范化** —— 所有提供商返回的缓存使用情况统一收敛到 `cacheRead/cacheWrite` 模型
4. **保持模型选择流程不受影响** —— `ILlmExecutionService` 接口完全不变

---

## 2. 架构设计：三层组件协作

### 2.1 组件拓扑

提示词缓存子系统由 **Gateway 层三个紧密协调的组件** + **Core 层可观测性基础设施** 组成：

| 层级 | 组件 | 职责 | 源码文件 |
|------|------|------|----------|
| Gateway | `PromptCacheCoordinator` | 请求塑形、指纹生成、方言路由 | `PromptCacheCoordinator.cs` |
| Gateway | `PromptCacheWarmService` | 后台保活、候选扫描、选择性预热 | `PromptCacheWarmService.cs` |
| Gateway | `PromptCacheTraceWriter` | JSONL 追踪、诊断输出、审计记录 | `PromptCacheTraceWriter.cs` |
| Core (支撑) | `PromptCacheUsageExtractor` | 提供商响应规范化 | `PromptCacheUsage.cs` |
| Core (支撑) | `ProviderUsageTracker` | 按提供商/模型的聚合追踪 | `ProviderUsageTracker.cs` |

### 2.2 核心数据结构

#### 2.2.1 PromptCacheDescriptor —— 缓存描述符

```csharp
internal sealed class PromptCacheDescriptor
{
    public required string SessionId { get; init; }           // 会话标识
    public required string ProfileId { get; init; }           // 模型配置文件 ID
    public required string ProviderId { get; init; }         // 提供商 ID
    public required string ModelId { get; init; }             // 模型 ID
    public required string Dialect { get; init; }             // 缓存方言: openai/anthropic/gemini/none
    public required string Retention { get; init; }           // 保留策略: none/short/long/auto
    public required string StableFingerprint { get; init; }   // 稳定指纹 (SHA256)
    public required string StableSystemPrompt { get; init; }   // 稳定系统提示词前缀
    public required string VolatileSuffix { get; init; }      // 易变后缀 (路由指令等)
    public required string ToolSignature { get; init; }       // 工具签名
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public bool Enabled { get; init; }                        // 是否启用
    public bool KeepWarmEligible { get; init; }              // 是否支持保活
}
```

**关键设计**: `StableFingerprint` 是整个缓存策略的核心——它基于 **providerId + modelId + stableSystemPrompt + toolSignature + responseFormat** 计算 SHA256 哈希，确保同一配置下的请求具有确定性缓存键。

#### 2.2.2 PromptCacheWarmRegistry —— 内存保活注册表

```csharp
internal sealed class PromptCacheWarmRegistry
{
    private readonly ConcurrentDictionary<string, PromptCacheWarmCandidate> _entries 
        = new(StringComparer.Ordinal);
    
    // 注册候选 (Record)
    // 快照扫描 (Snapshot)  
    // 标记已预热 (MarkWarmed)
    // 修剪失效条目 (Prune: 非活跃会话 / 超过6小时未更新)
}
```

**设计要点**:
- 使用 `ConcurrentDictionary` 保证线程安全
- Key 格式: `"{sessionId}:{profileId}"`
- 自动修剪机制: 非活跃会话或超过 6 小时未见的条目会被清除
- 不持久化——纯内存状态，重启后重建

---

## 3. 核心源码解析

### 3.1 PromptCacheCoordinator —— 请求塑形引擎

#### 3.1.1 请求准备流程 (Prepare 方法)

```csharp
public PromptCachePreparedRequest Prepare(
    Session session,
    ModelProfile profile,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    ChatOptions options)
{
    // Step 1: 解析方言和保留策略
    var dialect = ResolveDialect(profile.ProviderId, caching.Dialect);
    var retention = NormalizeRetention(caching.Retention);
    
    // Step 2: 提取系统提示词的稳定/易变部分
    var (stableSystem, volatileSuffix) = ExtractSystemPromptSegments(messages);
    
    // Step 3: 构建工具签名 (确定性排序 + JSON Schema 哈希)
    var toolSignature = BuildToolSignature(options);
    
    // Step 4: 计算稳定指纹
    var stableFingerprint = BuildStableFingerprint(
        profile.ProviderId, modelId, stableSystem, toolSignature, options.ResponseFormat);
    
    // Step 5: 克隆并增强 ChatOptions
    var preparedOptions = CloneOptions(options);
    
    // Step 6: 能力守卫 —— 三重条件检查
    if (caching.Enabled == true && 
        dialect != "none" && 
        profile.Capabilities.SupportsPromptCaching)
    {
        preparedOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        
        // 6.1 注入 OpenClaw 内部元数据键
        preparedOptions.AdditionalProperties["openclaw_prompt_cache_enabled"] = true;
        preparedOptions.AdditionalProperties["openclaw_prompt_cache_dialect"] = dialect;
        preparedOptions.AdditionalProperties["openclaw_prompt_cache_retention"] = retention;
        preparedOptions.AdditionalProperties["openclaw_prompt_cache_fingerprint"] = stableFingerprint;
        preparedOptions.AdditionalProperties["openclaw_prompt_cache_keep_warm"] = caching.KeepWarmEnabled == true;
        
        // 6.2 注入提供商特定键
        switch (dialect)
        {
            case "openai":
                preparedOptions.AdditionalProperties["prompt_cache_key"] = stableFingerprint;
                if (retention == "long")
                    preparedOptions.AdditionalProperties["prompt_cache_retention"] = "24h";
                break;
            case "anthropic":
                preparedOptions.AdditionalProperties["anthropic_cache_key"] = stableFingerprint;
                preparedOptions.AdditionalProperties["anthropic_cache_control"] = 
                    retention == "long" ? "1h" : "ephemeral";
                break;
            case "gemini":
                preparedOptions.AdditionalProperties["gemini_cached_content_key"] = stableFingerprint;
                break;
        }
    }
    
    // Step 7: 追踪写入
    _traceWriter.WriteRequest(descriptor, messages, preparedOptions);
    
    return new PromptCachePreparedRequest { Messages = messages, Options = preparedOptions, Descriptor = descriptor };
}
```

#### 3.1.2 系统提示词分段策略

```csharp
private const string RouteInstructionsMarker = "\n\n[Route Instructions]\n";

private static (string StableSystemPrompt, string VolatileSuffix) ExtractSystemPromptSegments(
    IReadOnlyList<ChatMessage> messages)
{
    var firstSystem = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
    if (string.IsNullOrWhiteSpace(firstSystem))
        return (string.Empty, string.Empty);

    // 以 [Route Instructions] 为界分割稳定/易变部分
    var markerIndex = firstSystem.IndexOf(RouteInstructionsMarker, StringComparison.Ordinal);
    if (markerIndex < 0)
        return (NormalizeText(firstSystem), string.Empty);

    return (
        NormalizeText(firstSystem[..markerIndex]),      // 稳定前缀 → 可缓存
        NormalizeText(firstSystem[(markerIndex + RouteInstructionsMarker.Length)..])); // 易变后缀
}
```

**设计洞察**: 通过硬编码的 `RouteInstructionsMarker` 分割系统提示词，OpenClaw 识别出路由指令等高频变化内容不应参与缓存，而基础人格定义、技能描述等稳定内容应被缓存。这种**语义感知的分割**比简单的前 N 个 token 缓存更精准。

#### 3.1.3 稳定指纹生成算法

```csharp
private static string BuildStableFingerprint(
    string providerId, string modelId, 
    string stableSystem, string toolSignature, 
    ChatResponseFormat? responseFormat)
{
    var responseFormatSignature = responseFormat is null 
        ? string.Empty 
        : responseFormat.GetType().FullName ?? responseFormat.ToString() ?? string.Empty;
    
    // 确定性拼接：provider | model | system | tools | format
    var payload = string.Join("\n---\n", 
        NormalizeText(providerId), NormalizeText(modelId), 
        stableSystem, toolSignature, responseFormatSignature);
    
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    return Convert.ToHexString(hash).ToLowerInvariant();
}
```

**关键保证**:
- `NormalizeText()` 统一换行符 (`\r\n` → `\n`) 并 Trim，消除平台差异
- 工具签名按名称排序，确保工具列表顺序不影响指纹
- 包含 ResponseFormat 类型全名，避免不同格式共用同一缓存

### 3.2 PromptCacheWarmService —— 保守的保活策略

#### 3.2.1 扫描周期与过滤漏斗

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try { await RunSweepAsync(stoppingToken); }
        catch (Exception ex) { _metrics.IncrementPromptCacheWarmFailures(); }
        
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // 每分钟扫描一次
    }
}

private async Task RunSweepAsync(CancellationToken ct)
{
    // 1. 获取活跃会话集合
    var activeSessionIds = (await _sessions.ListActiveAsync(ct))
        .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
    
    // 2. 修剪注册表 (非活跃 / 超过6小时)
    _warmRegistry.Prune(activeSessionIds, now - TimeSpan.FromHours(6));
    
    // 3. 对存活候选执行四层过滤
    foreach (var candidate in _warmRegistry.Snapshot())
    {
        // 过滤层 1: 会话必须仍活跃
        if (!activeSessionIds.Contains(candidate.Descriptor.SessionId)) { Skip(); continue; }
        
        // 过滤层 2: 配置文件必须有效且客户端就绪
        if (!_profiles.TryGetRegistration(...)) { Skip(); continue; }
        
        // 过滤层 3: 距离上次保活必须超过最小间隔 (默认55分钟，最低5分钟)
        var intervalMinutes = Math.Max(5, registration.Profile.PromptCaching.KeepWarmIntervalMinutes);
        if (candidate.LastWarmedAtUtc is not null && 
            now - candidate.LastWarmedAtUtc < TimeSpan.FromMinutes(intervalMinutes)) 
        { Skip(); continue; }
        
        // 过滤层 4: 执行保活调用 (MaxOutputTokens=1, Temperature=0，最小成本)
        await registration.Client.GetResponseAsync(candidate.WarmMessages, candidate.WarmOptions, ct);
        MarkWarmed();
    }
}
```

#### 3.2.2 保活调用的成本最小化设计

保活请求刻意最小化输出成本：
```csharp
WarmOptions = new ChatOptions
{
    ModelId = request.Options.ModelId,
    Tools = request.Options.Tools,           // 保留工具声明以缓存
    ResponseFormat = request.Options.ResponseFormat,
    AdditionalProperties = request.Options.AdditionalProperties?.Clone(),
    MaxOutputTokens = 1,                      // 仅请求 1 个 token
    Temperature = 0                           // 确定性输出
}
```

**设计意图**: 保活的目的不是获取有意义的响应，而是让上游提供商重新确认缓存条目的有效性。通过 `MaxOutputTokens=1` 和 `Temperature=0`，将保活成本压到最低。

### 3.3 PromptCacheUsageExtractor —— 提供商响应规范化

```csharp
public static class PromptCacheUsageExtractor
{
    // 四个已知的缓存写入键名 (跨提供商差异)
    private static readonly string[] CacheWriteKeys = [
        "cache_write_tokens",
        "cacheWriteTokens", 
        "cache_creation_input_tokens",
        "cacheCreationInputTokens"
    ];

    public static PromptCacheUsage FromUsage(UsageDetails? usage)
    {
        if (usage is null) return PromptCacheUsage.Empty;

        // 缓存读取: 使用标准 M.E.AI 字段
        var cacheRead = usage.CachedInputTokenCount ?? 0;
        
        // 缓存写入: 探测四个可能的键名
        long cacheWrite = 0;
        if (usage.AdditionalCounts is not null)
        {
            foreach (var key in CacheWriteKeys)
            {
                if (usage.AdditionalCounts.TryGetValue(key, out var value))
                { cacheWrite = value; break; }
            }
        }
        
        return new PromptCacheUsage(cacheRead, cacheWrite);
    }
}
```

**设计亮点**: 所有提供商特有的字段名差异被收敛到一个集中化的提取器中。Agent 运行时不需要知道提供商使用的是 `cache_creation_input_tokens` 还是 `cacheWriteTokens`——提取器负责统一翻译。

---

## 4. 配置体系：全局 + 逐字段覆盖

### 4.1 配置层级

```
Global Config (OpenClaw:Llm:PromptCaching)
    ↓ 逐字段合并 (field-by-field merge)
Profile Config (Profiles[].PromptCaching)
    ↓ 运行时解析
Effective Config (caching.Enabled + caching.Dialect + caching.Retention + ...)
```

**重要**: 配置文件级别的 `PromptCaching` 字段与全局设置是**逐字段合并**的，不是整体替换。如果配置文件仅设置了 `Enabled: true` 和 `Dialect: "anthropic"`，它仍会继承全局的 `Retention`、`KeepWarmEnabled` 等值。

### 4.2 配置模型

```csharp
public sealed class PromptCachingConfig
{
    public bool? Enabled { get; set; }                // 主开关 (默认 null → false)
    public string? Retention { get; set; }            // none | short | long | auto
    public string? Dialect { get; set; }              // auto | openai | anthropic | gemini | none
    public bool? KeepWarmEnabled { get; set; }        // 保活开关
    public int KeepWarmIntervalMinutes { get; set; } = 55;  // 保活间隔
    public bool? TraceEnabled { get; set; }           // 追踪开关
    public string? TraceFilePath { get; set; }        // 追踪文件路径
}
```

### 4.3 方言解析策略

```csharp
public static string ResolveDialect(string providerId, string? configuredDialect)
{
    var dialect = (configuredDialect ?? "auto").Trim().ToLowerInvariant();
    if (dialect != "auto") return dialect;

    // 自动推导映射表
    var provider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
    return provider switch
    {
        "openai" or "azure-openai" => "openai",
        "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock" => "anthropic",
        "gemini" or "google" => "gemini",
        _ => "none"
    };
}
```

---

## 5. 提供商适配矩阵

| 提供商 | 方言 | 缓存键策略 | 缓存读取 | 缓存写入 | 保活资格 | 特殊说明 |
|--------|------|-----------|---------|---------|---------|---------|
| OpenAI | `openai` | `prompt_cache_key` 确定性指纹 | ✅ `CachedInputTokenCount` | ✅ 仅报告时 | ❌ | `retention=long` → `24h` |
| Azure OpenAI | `openai` | 同 OpenAI | ✅ | ✅ | ❌ | |
| OpenAI 兼容 | `openai` (仅限显式) | 要求显式设置 Dialect | ✅ | ✅ | ❌ | `auto` 时发出警告 |
| Anthropic | `anthropic` | `anthropic_cache_key` + `cache_control` | ✅ `cache_read_input_tokens` | ✅ `cache_creation_input_tokens` | ✅ | retention=long → `1h` |
| Anthropic Vertex | `anthropic` | 同 Anthropic | ✅ | ✅ | ✅ | |
| Amazon Bedrock | `anthropic` | Claude 模型走 Anthropic 风格 | ✅ (仅限 Claude) | ✅ (仅限 Claude) | ✅ (仅限 Claude) | 非 Claude 模型 = 无缓存 |
| Gemini | `gemini` | `gemini_cached_content_key` | ✅ | ✅ | ✅ | |
| Ollama | `none` | v1 不支持 | ❌ | ❌ | ❌ | 能力标记不支持缓存 |
| 动态/插件 | 仅限显式 | 通过 `AdditionalProperties` 透传 | ✅ 如有报告 | ✅ 如有报告 | ❌ | 需显式声明方言 |

---

## 6. 可观测性设计：三级追踪体系

### 6.1 数据流分级

```
┌────────────────────────────────────────────────────────────────┐
│  LLM Response (UsageDetails)                                   │
│      ↓                                                         │
│  PromptCacheUsageExtractor.FromUsage()                         │
│      ↓ 统一化为 PromptCacheUsage (cacheRead, cacheWrite)       │
│      ├─→ RuntimeMetrics                    (全局聚合)          │
│      │       AddPromptCacheReads() / AddPromptCacheWrites()     │
│      │       暴露端点: /metrics/providers                       │
│      ├─→ ProviderUsageTracker               (按提供商-模型)     │
│      │       AddCacheTokens() + RecordTurn()                    │
│      │       最近 256 条轮次记录 (有界 ConcurrentQueue)          │
│      └─→ Session                            (按会话累计)        │
│              AddCacheUsage()                                    │
│              暴露: 会话状态摘要 /status /usage                  │
└────────────────────────────────────────────────────────────────┘
```

### 6.2 ProviderUsageTracker 回退机制

```csharp
public (long CacheReadTokens, long CacheWriteTokens) GetLatestSessionCacheTotals(string? sessionId)
{
    // 如果实时的会话缓存总计缺失，回退到历史记录
    var latest = _recentTurns.ToArray()
        .Where(item => item.SessionId == sessionId && 
                      (item.CacheReadTokens > 0 || item.CacheWriteTokens > 0))
        .OrderByDescending(item => item.TimestampUtc)
        .FirstOrDefault();

    return latest is null ? (0, 0) : (latest.CacheReadTokens, latest.CacheWriteTokens);
}
```

### 6.3 追踪输出 (JSONL)

启用条件: `TraceEnabled=true` 或环境变量 `OPENCLAW_CACHE_TRACE=1`

```json
{
  "timestampUtc": "2026-04-26T08:15:30Z",
  "event": "request",
  "sessionId": "sess_abc123",
  "profileId": "claude-research",
  "providerId": "anthropic",
  "modelId": "claude-sonnet-4.5",
  "dialect": "anthropic",
  "retention": "long",
  "fingerprint": "a1b2c3d4...",
  "messageCount": 12,
  "additionalProperties": {
    "openclaw_prompt_cache_enabled": "true",
    "anthropic_cache_key": "a1b2c3d4...",
    "anthropic_cache_control": "1h"
  }
}
```

---

## 7. 关键设计模式提炼

### 7.1 装饰器模式 (请求塑形)

`PromptCacheCoordinator` 不替换 `ChatOptions`，而是**装饰**它——通过 `CloneOptions()` 创建深拷贝，然后在 `AdditionalProperties` 上附加元数据。原始对象不受影响，下游组件可以安全地忽略这些附加属性。

### 7.2 策略模式 (方言路由)

`switch (dialect)` 块是典型的策略模式实现：相同的输入 (`PromptCacheDescriptor`) 根据方言选择不同的输出策略（OpenAI 键名、Anthropic 键名、Gemini 键名）。

### 7.3 注册表模式 (保活状态)

`PromptCacheWarmRegistry` 使用内存注册表管理保活候选，通过 `ConcurrentDictionary` 保证线程安全，通过 `Prune()` 实现有界状态管理。

### 7.4 适配器模式 (提供商规范化)

`PromptCacheUsageExtractor` 是适配器模式的应用：将多个提供商的不兼容响应格式适配为统一的 `PromptCacheUsage` 结构。

### 7.5 后台服务模式 (保活扫描)

`PromptCacheWarmService` 继承 `BackgroundService`，以每分钟一次的频率执行后台扫描。异常被捕获并记录为指标，不会导致服务崩溃。

---

## 8. 内部运维指南

### 8.1 诊断端点

| 端点 | 用途 |
|------|------|
| `/metrics/providers` | 按提供商-模型聚合的缓存读/写计数器 |
| `/doctor/text` | 验证不兼容的 `auto` 方言配置，输出警告 |
| 会话状态摘要 | 按会话累计的缓存使用情况 |
| `/status` 和 `/usage` | 格式化的缓存统计信息 |

### 8.2 常见问题排查

**Q: 启用了缓存但看不到缓存命中？**
- 检查 `profile.Capabilities.SupportsPromptCaching` 是否为 true
- 检查方言是否正确解析 (`ResolveDialect` 输出)
- 检查系统提示词是否包含可缓存的稳定前缀

**Q: 保活服务不工作？**
- 确认 `KeepWarmEnabled=true`
- 确认提供商在 `SupportsKeepWarm` 白名单中
- 检查 `KeepWarmIntervalMinutes` 是否 ≥ 5
- 查看 `_metrics` 中的 `PromptCacheWarmFailures` 计数

**Q: 缓存统计与提供商账单不一致？**
- OpenClaw 仅规范化提供商报告的数据，不会估算未报告的缓存写入
- 部分提供商（如 OpenAI）可能不报告 `cacheWrite`，此时显示为 0

### 8.3 环境变量速查

| 环境变量 | 说明 |
|----------|------|
| `OPENCLAW_CACHE_TRACE=1` | 强制启用缓存追踪 |
| `OPENCLAW_CACHE_TRACE_FILE=/path` | 指定追踪文件路径 |
| `OPENCLAW_CACHE_TRACE_PROMPT=0\|1` | 控制是否包含提示词文本 |
| `OPENCLAW_CACHE_TRACE_SYSTEM=0\|1` | 控制是否包含系统提示词 |

---

## 9. 可借鉴的设计要点

1. **零侵入增强**: 通过 `AdditionalProperties` 字典而非接口修改来扩展能力，保持向后兼容
2. **语义感知缓存**: 不是简单地缓存前 N 个 token，而是通过 `RouteInstructionsMarker` 识别语义边界
3. **确定性指纹**: 使用规范化输入 + 排序 + SHA256，确保相同配置产生相同缓存键
4. **保守保活**: 四层过滤 + 最小输出 token + 异常隔离，避免保活成为系统负担
5. **集中规范化**: 所有提供商差异收敛到单一提取器，运行时保持提供商无关
6. **有界状态**: `ConcurrentQueue` 限制 256 条记录，`Prune()` 清理 6 小时旧数据，防止内存泄漏

---

> **文档维护**: 本文档基于 OpenClaw.NET 2026-04-19 索引版本生成。后续版本更新时，重点关注 `PromptCacheCoordinator.cs` 中的方言 switch 块和 `PromptCacheUsageExtractor` 中的键名探测逻辑。
