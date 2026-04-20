# OpenClaw.NET 指标与遥测系统：全面可观测性设计

> 在分布式 AI Agent 系统中，可观测性不是奢侈品，而是运维的必需品。OpenClaw.NET 构建了完整的多层遥测体系——从无锁进程内计数器到单轮关联上下文，再到完整的 OpenTelemetry 集成——让系统在任何时刻都清晰可见。

## 设计哲学：拒绝单一的庞大 Metrics 类

OpenClaw.NET 的可观测性子系统刻意避免了"一切皆可观测"的笨重设计。相反，职责按照**粒度和作用域**进行划分：

| 组件 | 作用域 | 用途 |
|------|--------|------|
| `RuntimeMetrics` | 进程级 | 粗粒度全局计数器 |
| `TurnContext` | 单请求 | 细粒度轮次级统计 |
| `ProviderUsageTracker` | Provider/Model | 按维度汇总使用量 |
| `ToolUsageTracker` | 工具名 | 按工具执行汇总 |

这四个核心类型都暴露了 `Snapshot()` 方法，生成**无内存分配的结构体**，以便通过源生成上下文进行 JSON 序列化。

---

## 一、RuntimeMetrics：全局无锁计数器

`RuntimeMetrics` 是进程生命周期统计信息的中央累加器。每个字段都是 `long` 或 `int`，完全通过 `Interlocked` 或 `Volatile` 操作进行更新——在并行工具执行和多个会话的并发访问下**安全无锁**。

### 1.1 计数器指标

按领域划分的完整计数器：

| 领域 | 计数器 | 递增方法 |
|------|--------|----------|
| 请求入口 | `TotalRequests` | `IncrementRequests()` |
| LLM 调用 | `TotalLlmCalls` | `IncrementLlmCalls()` |
| Token 统计 | `TotalInputTokens` / `TotalOutputTokens` | `AddInputTokens()` / `AddOutputTokens()` |
| 工具执行 | `TotalToolCalls` | `IncrementToolCalls()` |
| 工具错误 | `TotalToolFailures` | `IncrementToolFailures()` |
| 工具超时 | `TotalToolTimeouts` | `IncrementToolTimeouts()` |
| LLM 弹性 | `TotalLlmRetries` | `IncrementLlmRetries()` |
| LLM 故障 | `TotalLlmErrors` | `IncrementLlmErrors()` |
| 审批流 | `ApprovalDecisionsRecorded` / `ApprovalDecisionsRejected` | 对应方法 |
| 会话管理 | `SessionEvictions` / `SessionCapacityRejects` | 对应方法 |
| 缓存命中 | `SessionCacheHits` / `SessionCacheMisses` | 对应方法 |
| 记忆召回 | `MemoryRecallSearches` / `MemoryRecallHits` | 对应方法 |
| 上下文压缩 | `MemoryCompactions` | `IncrementMemoryCompactions()` |
| 提示词缓存 | `PromptCacheReads` / `PromptCacheWrites` | 对应方法 |

### 1.2 仪表指标

除了单调递增的计数器，还有表示时间点值的仪表：

| 仪表 | 类型 | 语义 |
|------|------|------|
| `ActiveSessions` | `int` | 当前活跃会话数 |
| `CircuitBreakerState` | `int` | `0`=关闭, `1`=打开, `2`=半开 |
| `RetentionLastRunAtUnixSeconds` | `long` | 上次保留扫描的时间戳 |
| `RetentionLastRunDurationMs` | `long` | 上次保留扫描的持续时间 |
| `RetentionLastRunSucceeded` | `int` | 上次扫描是否成功 |

`Snapshot()` 方法生成 `MetricsSnapshot` 结构体——**刻意选择结构体而非类**，以避免在 AOT 编译路径上产生堆内存分配。`/metrics` 端点直接序列化的就是这个快照。

---

## 二、TurnContext：单请求关联与指标

`TurnContext` 是单请求的可观测性载体。每条传入消息都会创建一个 `TurnContext`，它伴随请求流经 Agent 循环、LLM 执行服务和工具执行器。

### 2.1 关联 ID 生成

第 19 行的 `CorrelationId` 属性遵循确定性的回退链：

1. 如果存在上游 Span（来自 ASP.NET Core 插桩），提取 **W3C TraceId**
2. 否则，从新 `Guid` 生成 16 字符的十六进制字符串

这意味着，如果配置了 OTLP 管道，每个轮次都会自动参与**分布式追踪**。

### 2.2 轮次级指标

`TurnContext` 为单个用户轮次跟踪两类指标：

| 属性 | 类型 | 更新者 |
|------|------|--------|
| `LlmCallCount` | `int` | `RecordLlmCall()` |
| `TotalInputTokens` | `long` | `RecordLlmCall()` |
| `TotalOutputTokens` | `long` | `RecordLlmCall()` |
| `TotalLlmLatency` | `TimeSpan` | `RecordLlmCall()` |
| `RetryCount` | `int` | `RecordRetry()` |
| `ToolCallCount` | `int` | `RecordToolCall()` |
| `TotalToolDuration` | `TimeSpan` | `RecordToolCall()` |
| `ToolFailureCount` | `int` | `RecordToolCall()` |
| `ToolTimeoutCount` | `int` | `RecordToolCall()` |

`ToString()` 重写生成日志聚合器友好的结构化摘要：

```
Turn[a1b2c3d4e5f67890] session=sess_abc channel=telegram llm=3 retries=1 tokens=4200in/890out tools=5 toolFails=0 toolTimeouts=1 llmLatency=3200ms toolDuration=450ms
```

---

## 三、ProviderUsageTracker：按 Provider/Model 维度汇总

`ProviderUsageTracker` 维护两个数据结构：

- **聚合计数器**：`ConcurrentDictionary<(ProviderId, ModelId), UsageCounter>`
- **轮次级详情**：`ConcurrentQueue<ProviderTurnUsageEntry>`（限制 256 项）

### 聚合计数器方法

| 方法 | 记录内容 |
|------|----------|
| `RecordRequest()` | 请求计数 |
| `RecordRetry()` | 重试计数 |
| `RecordError()` | 错误计数 |
| `AddTokens()` | 输入/输出 Token |
| `AddCacheTokens()` | 缓存读取/写入 Token |

缺失的 `provider` 回退到 `"unknown"`，缺失的 `model` 回退到 `"default"`——确保维度键始终有效。

### 轮次级记录

`RecordTurn()` 方法捕获完整的轮次摘要，包括：

- 会话 ID、通道 ID、Provider/Model
- 所有 Token 类别
- InputTokenComponentEstimate 细分

---

## 四、ToolUsageTracker：按工具执行汇总

`ToolUsageTracker` 是一个以工具名称为键的 `ConcurrentDictionary<string, ToolUsageCounter>`。

### 无锁Duration累加

通过 **double-as-long 位模式的 CAS 循环**累加总持续时间——这是一种无锁技术，将 `double` 转换为其 `Int64Bits` 表示形式以进行原子比较并交换。

### ToolUsageSnapshot

每个快照暴露：

- `ToolName` / `Calls` / `Failures` / `Timeouts`
- `TotalDurationMs`

结果按调用次数降序排序，然后按工具名称字母顺序排序。

---

## 五、PromptCacheUsage：缓存 Token 提取

`PromptCacheUsage` 是一个只读记录结构体：

```csharp
(long CacheReadTokens, long CacheWriteTokens)
```

配套的 `PromptCacheUsageExtractor` 跨 Provider 标准化缓存 Token 提取：

- **缓存读取 Token**：直接取自 `UsageDetails.CachedInputTokenCount`
- **缓存写入 Token**：探测 `AdditionalCounts` 中的四个已知键变体：
  - `cache_write_tokens`
  - `cacheWriteTokens`
  - `cache_creation_input_tokens`
  - `cacheCreationInputTokens`

---

## 六、RuntimeEventStore：结构化 JSONL 事件日志

`RuntimeEventStore` 在 `{StoragePath}/admin/runtime-events.jsonl` 写入**仅追加的 JSONL 文件**。

### 事件分类法

每个条目携带：

- 关联 ID
- 会话/通道/发送者上下文
- `Component` + `Action` 分类法
- 严重级别（Information/Warning/Error）
- 摘要文本
- 自由格式元数据字典

### LLM 生命周期事件

| 组件 | 动作 | 触发条件 |
|------|------|----------|
| `llm` | `route_selected` | Provider/Model 已解析 |
| `llm` | `request_started` | 非流式请求已启动 |
| `llm` | `request_completed` | 已收到非流式响应 |
| `llm` | `request_failed` | 非流式请求出错 |
| `llm` | `stream_started` | 流式请求已启动 |
| `llm` | `stream_completed` | 流式成功完成 |
| `llm` | `stream_failed` | 流式遇到错误 |

**关键设计**：写入失败通过 `RuntimeMetrics.IncrementRuntimeEventWriteFailures()` 跟踪，而不是抛出异常——事件存储是**尽力而为**的，绝不能导致请��路径崩溃。

---

## 七、OpenTelemetry 集成

在 Gateway 启动期间调用的**单一扩展方法**中配置完整的 OpenTelemetry 信号三要素。

### 配置入口

```
AddOpenClawObservability → AddGatewayTelemetry
```

### 默认导出

默认情况下，所有三种信号都通过 **OTLP 导出器**导出，可通过标准环境变量配置：

| 环境变量 | 用途 | 默认值 |
|----------|------|--------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | 收集器 URL | `http://localhost:4317` |
| `OTEL_EXPORTER_OTLP_HEADERS` | 认证标头 | — |
| `OTEL_SERVICE_NAME` | 服务名称 | `OpenClaw.Gateway` |

### 自定义源

| 信号 | 自动插桩 | 自定义源 |
|------|----------|----------|
| 链路追踪 | `AspNetCoreInstrumentation`, `HttpClientInstrumentation` | `Telemetry.ActivitySource` |
| 指标 | 同上 + `RuntimeInstrumentation` | Meter `"OpenClaw.Gateway"` |
| 日志 | `ILogger` → OpenTelemetry 桥接 | `ILogger` 结构化日志 |

`AddGatewayTelemetry` 方法会**清除所有现有 LoggingProvider 注册**，并替换为 OpenTelemetry 日志导出器。

---

## 八、诊断 HTTP 端点

所有诊断端点都需要**操作员身份验证**（Bearer Token 或浏览器会话 Cookie）。

### 端点参考

| 端点 | 方法 | 响应类型 | 关键数据 |
|------|------|----------|----------|
| `/health` | GET | `HealthResponse` | 状态、运行时间 |
| `/metrics` | GET | `MetricsSnapshot` | 所有 40 个全局计数器 + 仪表 |
| `/metrics/providers` | GET | `ProviderUsageSnapshot[]` | 按 Provider/Model 汇总 |
| `/metrics/tools` | GET | `ToolUsageSnapshot[]` | 按工具的调用/失败/超时/持续时间 |
| `/doctor` | GET | JSON 对象 | 完整系统诊断报告 |
| `/doctor/text` | GET | `text/plain` | 人类可读诊断报告 |
| `/memory/retention/status` | GET | `RetentionStatusResponse` | 数据保留扫描状态 |
| `/memory/retention/sweep` | POST | `RetentionSweepResponse` | 触发扫描（支持 dryRun） |

### /doctor 端点

生成涵盖九个子系统的统一报告：

1. 绑定/认证配置
2. 工具策略
3. 通道就绪状态
4. 允许列表
5. 配对
6. 内存/保留状态
7. 定时任务
8. 运行时模式
9. 插件健康状态
10. 技能
11. 安全态势
12. 完整使用量摘要

`/doctor/text` 变体在底部附带**"建议的后续步骤"**指导，专为终端消费和脚本设计。

> 两个 doctor 端点在每次请求时都会刷新动态状态（无缓存），确保反映当前 `CircuitBreakerState`、`ActiveSessions` 和最新保留扫描结果。

---

## 九、遥测流：请求穿过系统的路径

```
请求入口 → 关联ID + TurnContext + RuntimeMetrics.IncrementRequests()
    ↓
LLM执行 → GatewayLlmExecutionService 路由选择/请求/重试/错误
    ↓
Agent循环 → TurnContext.RecordLlmCall()
    ↓
工具执行 → OpenClawToolExecutor + ToolUsageTracker + AuditLogHook
    ↓
轮次完成 → TurnContext.ToString() + ProviderUsageTracker.RecordTurn() + RuntimeEventStore
    ↓
定期操作 → 保留扫描/记忆召回/提示词缓存预热
```

---

## 十、调试清单

### 诊断延迟问题

1. 从 `/metrics/tools` 找到 `TotalDurationMs` 最高的工具
2. 与 `/metrics/providers` 交叉引用获取 LLM 侧延迟
3. 查询 `/doctor/text` 获取断路器状态和保留状态全貌

### 按会话取证分析

使用带会话 ID 过滤器的 `RuntimeEventStore.Query()` API。

---

## 小结

OpenClaw.NET 的可观测性设计体现了**纵深防御**思维：

- **无锁并发** — `Interlocked` / `Volatile` 确保计数器线程安全
- **分层设计** — RuntimeMetrics → TurnContext → Provider/Tool 维度
- **尽力存储** — 事件写入失败不阻塞请求路径
- **OpenTelemetry 原生** — 通过标准 OTLP 管道无缝导出

从进程级全局统计到单请求粒度关联，OpenClaw.NET 让故障排查和容量规划变得**有据可依**。

---

*文档来源：https://zread.ai/clawdotnet/openclaw.net/29-metrics-and-telemetry*