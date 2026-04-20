# OpenClaw 会话管理系统

> 来源: https://zread.ai/clawdotnet/openclaw.net/24-session-management

## 概述

OpenClaw 中的每一次对话——无论发起自 Telegram、Slack、WebSocket 客户端还是定时任务——都由一个 **Session** 表示。会话管理（Session management）是负责创建、追踪、持久化、过期处理和分支管理这些对话的子系统。它位于消息管道、Agent 运行时和持久化层的交汇处，确保多轮对话即使在进程重启、渠道切换和容量受限的情况下也能保持连贯。

---

## 1. Session 模型

`Session` 是一个自包含的对话状态单元。其核心持有一个确定性键、渠道/发送者身份、可变的 `ChatTurn` 条目历史记录以及累计的 Token 使用计数器。在此基础状态之上，该模型还承载了几个**路由作用域覆盖配置**，允许网关路由和按会话的用户命令微调 Agent 的行为，而无需修改全局配置。

```
每个 Session 跟踪四个 Token 计数器：
- PromptTokens        // 提示词 Token
- CompletionTokens   // 补全 Token  
- ContractBaselineInputTokens  // 合约基线输入
- ContractBaselineOutputTokens // 合约基线输出
```

使用 `Interlocked` 操作来保证在多个管道阶段并发更新时的线程安全，且无需加锁。这些计数器会输入到合约治理中，其中 `ContractBaselineInputTokens` 和 `ContractBaselineOutputTokens` 会在附加合约时快照计数器值，从而在合约的整个生命周期内实现成本增量跟踪。

---

## 2. SessionManager：内存权威

`SessionManager` 是所有会话操作的中央线程安全协调器。它在内存中维护一个 `ConcurrentDictionary<string, Session>` 作为热缓存，并以 `IMemoryStore` 作为持久化后端。

来自 `GatewayConfig` 的两个关键配置值决定了其行为：
- `SessionTimeoutMinutes`：控制空闲过期窗口
- `MaxConcurrentSessions`：对并发内存会话强制执行硬性上限

### 2.1 会话创建与键解析

会话通过两种访问模式按需创建，并由单一的准入路径统一处理。

| 访问模式 | 方法 | 说明 |
|---------|------|------|
| 标准路径 | `GetOrCreateAsync(channelId, senderId)` | 确定性键派生为 `channelId:senderId` |
| 显式路径 | `GetOrCreateByIdAsync(sessionId, channelId, senderId)` | 接受调用者选择的键，适用于定时任务、Webhook 和生成的子 Agent 会话 |

这两条路径都通过单一的**准入门**（并发度为 1 的 `SemaphoreSlim`）进行分流。双重检查锁定模式：
1. 首先是无锁的 `TryGetValue`
2. 然后是门控重新检查
3. 接着是存储查找
4. 最后是创建

这确保了在并发访问下的正确性，同时避免了对缓存命中进行不必要的串行化。在任何新会话进入活动字典之前，`EnsureCapacityForAdmission()` 会运行一个两阶段容量检查，首先扫描过期的会话，然后如果仍然超过上限，则通过 LRU 淘汰最近最少活动的会话。

### 2.2 带重试的持久化

会话持久化使用**带三次重试的指数退避策略**：
- 第一次重试：100 毫秒
- 第二次重试：200 毫秒
- 第三次重试：400 毫秒

该方法接受一个 `sessionLockHeld` 参数，当调用者已持有该会话的按键信号量时，可避免冗余的锁获取——这是编码后端层中分支和所有者会话同步在内部使用的一项优化。

> **注意**：在实现自定义的 `IMemoryStore` 后端时，请注意 `SessionManager.PersistAsync` 可能会针对不同会话并发调用 `SaveSessionAsync`，但**绝不会针对同一会话并发调用**（由按会话锁定强制执行）。你的存储实现只需要处理单会话的写入并发。

### 2.3 按会话锁定

每个活动会话可以通过 `AcquireSessionLockAsync` 获取一个独占的 `SemaphoreSlim`。此锁与准入门分离，且用于不同的目的：它将对单个会话的状态变更操作（分支、持久化和所有者会话历史追加）进行串行化。

该锁作为 `IAsyncDisposable` 租约返回，管理器会定期运行 `CleanupSessionLocksOnce`，以对已从活动字典中被淘汰的会话进行孤立信号量的垃圾回收。

### 2.4 容量限制与 LRU 淘汰

当配置了 `MaxConcurrentSessions`（任何大于 0 的值）时，准入路径通过三步级联操作强制执行容量限制：

1. **首先扫描过期会话**
2. **如果仍达到容量上限，则通过 O(n) 扫描淘汰最近最少活动的会话**
3. **如果仍然超限，则抛出 `InvalidOperationException` 并增加容量拒绝指标**

被淘汰的会话会转换为 `SessionState.Expired` 状态，并在移除前尽力持久化到存储中。

> **优化提示**：代码中包含一个 TODO，指出对于具有数百个并发会话的部署，使用 `PriorityQueue<LRUEntry>` 可将淘汰操作的时间复杂度降低到 O(log n)。

---

## 3. 对话分支

OpenClaw 支持**命名对话分支**——会话历史的某个时间点快照，可以在稍后恢复。这支持了"假设性"探索：Agent（或用户通过工具调用）可以保存当前的对话状态，尝试替代方案，然后在需要时恢复到原始分支。

### BranchAsync

`BranchAsync` 方法将整个 `History` 列表深拷贝到一个带有确定性分支 ID（`sessionId:branch:name:ticks`）的 `SessionBranch` 记录中，并通过 `IMemoryStore.SaveBranchAsync` 进行持久化。

### RestoreBranchAsync

`RestoreBranchAsync` 在会话锁的保护下，原子性地清空会话的当前历史并将其替换为分支快照。

### BuildBranchDiffAsync

`BuildBranchDiffAsync` 方法在当前会话历史与某个分支之间计算**共享前缀分析**，并行遍历两个历史记录直到第一个分歧点。它返回两侧分歧部分的轮次摘要，使 Agent 能够呈现自分支点以来发生了什么变更的、人类可读的差异对比。

---

## 4. 持久化层：IMemoryStore

`IMemoryStore` 接口定义了会话和分支存储的持久化契约。

### 默认实现

| 实现 | 说明 |
|------|------|
| `FileMemoryStore` | 磁盘上的 JSON 文件，本地优先 |
| `SqliteMemoryStore` | 适用于需要更丰富查询的部署的 SQLite 数据库 |

两者实现了相同的接口，因此 `SessionManager` 并不关心哪个后端处于活动状态。

### 操作方法

| 方法 | 使用者 | 说明 |
|------|-------|------|
| `GetSessionAsync` | SessionManager | 在缓存未命中时按 ID 加载会话 |
| `SaveSessionAsync` | PersistAsync（重试循环） | 保存会话 |
| `SaveBranchAsync` | BranchAsync | 保存分支快照 |
| `LoadBranchAsync` | RestoreBranchAsync, BuildBranchDiffAsync | 加载分支 |
| `ListBranchesAsync` | ListBranchesAsync 透传 | 列出分支 |
| `DeleteBranchAsync` | 管理清理 | 删除分支 |

---

## 5. 会话元数据存储

除了核心会话数据，OpenClaw 还维护一个单独的**元数据存储**（`SessionMetadataStore`），用于保存面向用户的注解：星标状态、标签、活动预设 ID 和待办事项。

此元数据存放在 `admin/session-metadata.json` 中，并且独立于会话生命周期进行管理。它支持**部分更新**——仅设置 `Starred` 标志会保留现有的标签和待办事项，反之亦然。

### 待办事项标准化逻辑

- 没有 ID 的项会自动生成带有 GUID 前缀的 ID（`todo_{guid}` 截断为 17 个字符）
- 去除空白字符
- 列表会先按未完成项优先、再按创建日期排序——为用户提供自然的任务列表顺序

---

## 6. 会话管理列表与搜索

管理层提供了两种互补的查询机制用于会话发现。

### 6.1 管理列表 (ISessionAdminStore)

通过 `SessionListQuery` 进行的分页列表支持按渠道、发送者、时间范围、状态、星标状态和标签进行过滤。

**关键优化**：当查询涉及元数据感知过滤器（`Starred` 或 `Tag`）时，系统必须首先加载**所有**已持久化的页面，应用元数据匹配，然后在内存中进行分页——因为星标/标签数据存放在单独的 `SessionMetadataStore` 中，而不在管理存储的索引中。

### 6.2 全文搜索 (ISessionSearchStore)

`SessionSearchTool` 通过 `ISessionSearchStore` 提供跨对话历史的相关性排名搜索。查询包括自由文本、渠道/发送者范围、时间边界以及可配置的摘要长度。

结果返回带有评分的 `SessionSearchHit` 条目，包含匹配的角色、时间戳和文本摘要——使 Agent 能够通过语义内容而非仅仅通过元数据来定位先前的对话。

### 过滤维度对比

| 维度 | 管理列表 | 全文搜索 |
|------|---------|---------|
| 渠道 / 发送者 | ✅ | ✅ |
| 时间范围 | ✅ (FromUtc, ToUtc) | ✅ (FromUtc, ToUtc) |
| 会话状态 | ✅ | ❌ |
| 星标 / 标签 | ✅ (元数据关联) | ❌ |
| 自由文本内容 | ✅ (ID/发送者/标签匹配) | ✅ (相关性排名) |
| 分页 | ✅ (page/pageSize) | ✅ (limit) |

---

## 7. 用于会话编排的 Agent 工具

网关注册了一套具备会话感知能力的工具，使 Agent 本身能够编排多会话工作流。

### 7.1 sessions_spawn

创建一个带有自动生成 ID（`spawn_{guid}` 截断为 20 个字符，或调用者提供的 ID）的新子 Agent 会话，并通过 `MessagePipeline` 注入初始提示词。

生成的会话默认 `channelId` 为 `"agent"`，`senderId` 为 `"system"`，将其标记为 Agent 发起的会话而非用户对话。

### 7.2 sessions_send

通过管道向另一个活动会话发送即发即弃的消息。该消息作为系统发起的 `InboundMessage` 注入——目标会话会通过完整管道处理它，但发送者身份为 `"system"`。

### 7.3 sessions_yield

带有响应捕获的同步跨会话调用。Agent 向目标会话发送消息，然后**轮询**新的助手轮次，使用逐步增加的退避策略（500 毫秒 → 上限 2 秒）。

- 它显式地防止了自产出死锁
- 如果在轮询期间目标会话被从活动缓存中淘汰，则会回退到磁盘查找
- 超时时间默认为 60 秒，最大上限为 300 秒

> **性能注意**：`sessions_yield` 工具在每次轮询周期中通过 `TryGetActiveById` 实现 O(n) 扫描。在高会话数量的部署中，在设置较长超时值时应考虑此轮询成本。

### 7.4 session_status

返回会话当前状态的紧凑诊断快照：
- 会话 ID、状态枚举、渠道、发送者
- 轮次数、Token 使用量（输入/输出）
- 提示词缓存统计信息、时间戳、活动时长
- 以及任何模型覆盖配置

缓存 Token 总数来源于会话自身的计数器，或作为后备来源于 `ProviderUsageTracker`。

### 7.5 sessions_history

获取任何会话（活动或已持久化）的对话记录，返回最近的 N 轮（默认 20，最大 100）及其角色、时间戳和内容。

它会先检查活动缓存，然后再回退到 `IMemoryStore`。

### 7.6 session_search

跨所有已持久化的对话历史进行全文搜索，返回带有摘要的相关性评分命中结果。范围可以通过渠道、发送者和时间范围缩小。

---

## 8. 编码后端会话

除了标准会话模型，编码后端子系统引入了一个并行的会话概念：`BackendSessionRecord`。

这些代表了长期运行的编码 Agent 会话（连接到如 Claude Code 或 Aider 等外部编码后端），它们**归属于**一个主 OpenClaw 会话。

- `BackendSessionCoordinator` 编排它们的生命周期
- `BackendSessionRuntime` 内部类通过将每个 `BackendEvent` 类型映射到带有 `[backend:{id}]` ��缀的适当 `ChatTurn`，将后端事件同步回所有者会话的历史中

### 事件流

`BackendSessionEventStreamStore` 使用有界的 `Channel<BackendEvent>` 实例（容量 64，丢弃最旧策略）提供实时事件订阅模型。这使得编码后端事件能够通过 SSE/WebSocket 实时流式传输到连接的客户端。

---

## 9. 配置参考

| 配置键 | 类型 | 默认值 | 描述 |
|-------|------|-------|------|
| `SessionTimeoutMinutes` | `int` | (来自配置) | 会话过期并从活动缓存中淘汰前的空闲时长 |
| `MaxConcurrentSessions` | `int` | (来自配置) | 同时处于活动状态的内存会话硬性上限（0 = 无限制） |

> **注意**：这些值从 `GatewayConfig` 流入 `SessionManager` 构造函数。在生产环境调优时需注意，过期会话仅在下次准入尝试或显式调用 `SweepExpiredActiveSessions` 时才会被淘汰——`SessionManager` 本身没有后台计时器。该扫描由网关的定期健康检查从外部触发。

---

## 10. 生命周期总结

```
┌─────────────────────────────────────────────────────────────────┐
│                    消息接收                              │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────────────┐
│              SessionManager.GetOrCreateAsync                   │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ 1. 准入门 (SemaphoreSlim, 并发度=1)                 │ │
│  │ 2. 双重检查锁定                                       │ │
│  �� 3. 容量检查 + LRU 淘汰                               │ │
│  └─────────────────────────────────────────────────────┘ │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────────────┐
│                    活动会话 (内存缓存)                           │
└─────────────────────┬───────────────────────────────────┘
                      ▼
         ┌────────────┴────────────┐
         ▼                         ▼
┌─────────────────┐    ┌─────────────────────────┐
│ 用户交互/Agent   │    │ 持久化 (后台)           │
│ 执行 Tool       │    │ - 即发即弃 QueueBestEffort│
│ - 会话锁保护     │    │ - 三次重试指数退避      │
└─────────────────┘    └─────────────────────────┘
```

---

## 相关文档

- **[记忆回忆与保留](/25-memory-recall-and-retention)** — 会话如何与长期记忆存储交互以实现跨会话的知识持久化
- **[Agent 循环与工具执行](/7-agent-loop-and-tool-execution)** — Agent 运行时如何在每一轮中消费会话历史
- **[工具执行后端](/17-tool-execution-backends)** — 编码后端会话如何与会话所有权模型集成
- **[指标与遥测](/29-metrics-and-telemetry)** — 暴露给可观测性系统的会话淘汰与容量拒绝指标