# `PublishTokenUsageEvent` 与 `TurnTokenUsageRecord` 双通道设计解析

> 适用代码：[src/OpenClaw.Agent/MafExecutionServiceChatClient.cs](src/OpenClaw.Agent/MafExecutionServiceChatClient.cs)
> 涉及文件：[src/OpenClaw.Agent/TokenUsageEventMapper.cs](src/OpenClaw.Agent/TokenUsageEventMapper.cs) · [src/OpenClaw.TokenHubSink/Observability/TokenUsageEvents.cs](src/OpenClaw.TokenHubSink/Observability/TokenUsageEvents.cs) · [src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs](src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs) · [src/OpenClaw.Agent/MafExecutionContext.cs](src/OpenClaw.Agent/MafExecutionContext.cs) · [src/OpenClaw.Core/Observability/ProviderUsageTracker.cs](src/OpenClaw.Core/Observability/ProviderUsageTracker.cs) · [src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs](src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs)

---

## 一、选中的代码段

```csharp
// 位于 MafExecutionServiceChatClient.cs:169（静态方法）
private static void PublishTokenUsageEvent(
    MafExecutionContext executionContext,
    string providerId,
    string modelId,
    long inputTokens,
    long outputTokens,
    long cacheReadTokens)
{
    if (executionContext.TokenUsageEventSink is not { } sink || sink is NullTokenUsageEventSink)
        return;

    sink.Publish(TokenUsageEventMapper.Create(
        executionContext.Session,
        executionContext.TokenUsageAgentId,
        providerId,
        modelId,
        inputTokens,
        outputTokens,
        cacheReadTokens));
}
```

以及它被调用的位置（`RecordUsage` 内部，紧跟在 `Session.AddTokenUsage / AddCacheUsage` 之后）：

```csharp
// 1) 先更新进程内 Session 的运行累计
executionContext.Session.AddTokenUsage(resolvedInputTokens, resolvedOutputTokens);
executionContext.Session.AddCacheUsage(cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);

// 2) 旁路推一份事件给 TokenHub 采集器（关键：此时 session_total_* 已是最新）
PublishTokenUsageEvent(
    executionContext, providerId, modelId,
    resolvedInputTokens, resolvedOutputTokens, cacheUsage.CacheReadTokens);

// 3) 再创建一份 Turn 级别记录走进程内记账
var record = new OpenClaw.Core.Models.TurnTokenUsageRecord { ... };
if (executionContext.TurnTokenUsageObserver is not null)
{
    executionContext.TurnTokenUsageObserver.RecordTurn(record);
    return;
}
_providerUsage.RecordTurn(/* ... */);
```

---

## 二、这段代码到底在做什么（要点拆解）

| 关注点 | 答案 |
|---|---|
| 方法签名上的 `static` | 它只读 `executionContext` 传进来的字段，不访问任何实例字段 → 节省隐藏的 this 引用和虚调用成本（热路径优化） |
| `TokenUsageEventSink is not { } sink` | 模式匹配先做 null 守卫；为空直接 `return`，**零分配** |
| `sink is NullTokenUsageEventSink` | `NullTokenUsageEventSink.Instance` 是单例 no-op sink；显式拦截避免做 `TokenUsageEventMapper.Create` 分配 |
| `TokenUsageEventMapper.Create(...)` | 把"这一通 LLM 调用的用量"映射成**线协议（wire-format）事件** `SessionTokenUsageEvent`，并附上"会话当前总用量"快照字段（`session_total_*`） |
| `sink.Publish(...)` | 注释里明确写了"never blocks"：实现只把事件**塞进有界内存 channel**，后台 worker 异步刷出；**永远不会卡 LLM 热路径** |

---

## 三、核心问题：为什么用 `PublishTokenUsageEvent()`，而不是直接用 `var record = new TurnTokenUsageRecord`？

**一句话回答：两者根本不是"二选一"的关系，而是两条并行通道，目标、格式、消费者完全不同。**

### 3.1 两条通道的对照表

| 维度 | `PublishTokenUsageEvent`（旁路） | `var record = TurnTokenUsageRecord`（进程内记账） |
|---|---|---|
| **目标消费者** | KingCrab 沙箱**外部**的 `TokenHub.Collector`（再经 Kafka → Doris 汇聚） | KingCrab 沙箱**内部**：`TurnTokenUsageObserver` 链 / `ProviderUsageTracker` |
| **数据类型** | `SessionTokenUsageEvent`（snake_case JSON，跨进程线协议） | `TurnTokenUsageRecord`（C# record，进程内结构体） |
| **字段差异** | 含 `session_total_*` 快照、**不含** `CacheWriteTokens` | 含 `CacheWriteTokens` / `IsEstimated` / `EstimatedInputTokensByComponent` |
| **传输方式** | 序列化进有界 channel → 后台 worker 异步发往 TokenHub | 进程内同步调用观察者（审计/累计/最近 256 turn 队列） |
| **职责** | **出沙箱**做长期、跨实例的用量汇聚 | **沙箱内**做实时累计、最近 turn 缓存、审计日志 |
| **失败容忍** | 丢一条事件无伤大雅（采样/告警可补救） | 影响本进程观测正确性，错误需要 try/catch 隔离 |
| **放置位置** | Session 累计**之后**（保证快照新鲜） | `PublishTokenUsageEvent` 之后（不依赖任何下游副作用） |
| **典型消费侧** | Doris 按 session SUM 增量、看板、对账 | 健康检查、单元测试、内存审计日志 |

### 3.2 一张图看清流程

````mermaid
flowchart TD
    A[LLM 调用完成<br/>sw.Stop] --> B[RecordUsage]
    B --> B1[executionContext.Session<br/>.AddTokenUsage]
    B1 --> B2[executionContext.Session<br/>.AddCacheUsage]
    B2 --> C{TokenUsageEventSink<br/>有效且非 NoOp?}
    C -- 否 --> D[跳过旁路推送<br/>零分配]
    C -- 是 --> E[TokenUsageEventMapper.Create<br/>构造 SessionTokenUsageEvent]
    E --> F[sink.Publish<br/>入有界 channel]
    F --> G[后台 worker<br/>→ TokenHub.Collector<br/>→ Kafka → Doris]
    B2 --> H[new TurnTokenUsageRecord]
    H --> I{TurnTokenUsageObserver<br/>不为 null?}
    I -- 是 --> J[Observer.RecordTurn<br/>try/catch 隔离]
    I -- 否 --> K[ProviderUsageTracker<br/>.RecordTurn 落 256 队列]
    J --> L[_providerUsage.AddTokens/AddCacheTokens]
    K --> L
    L --> M[_metrics 累加]
    M --> N[LogDebug]
````

### 3.3 关键设计：为什么顺序必须是 "Session 累计 → PublishTokenUsageEvent → record"？

看 `TokenUsageEventMapper.Create` 的实现：

```csharp
return new SessionTokenUsageEvent
{
    ...
    InputTokens            = inputTokens,                  // 本通增量（可 SUM）
    OutputTokens           = outputTokens,
    CacheReadTokens        = cacheReadTokens,
    SessionTotalInputTokens    = session.TotalInputTokens,   // ★ 会话快照（不可 SUM）
    SessionTotalOutputTokens   = session.TotalOutputTokens,
    SessionTotalCacheReadTokens = session.TotalCacheReadTokens,
    SessionTotalTokens          = session.GetTotalTokens(),
};
```

`session_total_*` 字段是**会话语义下的累计快照**，而下游 Doris 报表**只对增量字段做 SUM**。如果 `PublishTokenUsageEvent` 跑在 `AddTokenUsage` 之前，发出去的事件里 `session_total_*` 就少算了这一通——下游对账会"少 1"。所以代码里特意写了注释：

> Runs after the Session running totals above are updated so the snapshot fields are current.

---

## 四、通俗讲解（写给中级开发工程师）

### 4.1 一句话比喻

把 `MafExecutionServiceChatClient.RecordUsage` 想成**餐厅收银台**：

- **进程内记账 (`record`)** = **本餐厅的当日流水本**：服务员每次点完菜，会在小本本上记一行"桌 3、牛肉面、35 元"。这本流水本**只在本餐厅用**，用来对账、做今天的营业报告。
- **旁路推送 (`PublishTokenUsageEvent`)** = **总部财务系统的对账单**：总部想知道全国每家店每卖一份菜的明细，但它不直接拿你的小本本——它要求你**按统一格式填一张对账单**（`SessionTokenUsageEvent`），然后**投到总部的收件箱**（`sink.Publish`）。**收件箱是异步的**——你往里一丢就走，后台有专人按时间把对账单汇总到总部 ERP。

两个动作都发生在客人结账那一刻，**都不可或缺**：本餐厅要记账，总部要汇总。

### 4.2 三个关键设计点

**① 为什么不直接用 `record` 代替 `PublishTokenUsageEvent`？**

`record` 是**店内小本本**的格式——`CacheWriteTokens`、`IsEstimated`、`EstimatedInputTokensByComponent` 这些字段对总部完全没用，反而占带宽；而总部要看的"截至此刻本桌已累计消费多少"这种**累计快照**字段，`record` 又**没有**。两边数据 schema 都不一样，怎么替？

强行用一份结构去喂两边只会出现两种坏味道：
- 给总部塞一堆它不看的字段（脏）
- 给店里记账漏掉 `CacheWriteTokens`（错）

干脆**各做一份**，各自最优。

**② `static` 修饰 + `NullTokenUsageEventSink` 拦截 = 零分配降级**

这段代码在 **LLM 每次响应结束都会执行一次**，是**最热的热路径**之一。所以做了两件事：

1. `private static` —— 不需要 `this` 引用，JIT 可以更激进内联。
2. `executionContext.TokenUsageEventSink is not { } sink || sink is NullTokenUsageEventSink` —— 这条守卫**写在最前面**。一旦发现"没启用旁路"或"用了 no-op"，**直接 return**，连 `TokenUsageEventMapper.Create` 的对象分配都不发生。

这就是**配置即插拔、成本即归零**的典型写法：未启用时一条 `if` 就把整条路径消掉，没有任何"先 new 再丢弃"的浪费。

**③ `sink.Publish` 注释里那句"never blocks"是合同**

如果哪天有人改实现把 `Publish` 改成同步 HTTP/Kafka 发送，**整个 LLM 响应延迟都会变差**。所以接口注释明确写：

> Publish is invoked on the LLM hot path, so only in-memory enqueueing is allowed.

这是**用注释保护架构不变量**的写法——提醒后来者"你可以改实现，但绝不能同步 I/O"。

### 4.3 一张图总结"为什么是两条通道"

````mermaid
graph LR
    subgraph Sandbox[KingCrab 沙箱进程]
        R[RecordUsage] --> SP[Session 累计]
        SP --> PB[PublishTokenUsageEvent]
        SP --> TR[TurnTokenUsageRecord]
        PB --> CH[有界 channel<br/>零阻塞]
        TR --> OB[Observer 链]
        TR --> PT[ProviderUsageTracker<br/>最近 256 turn]
    end
    CH -. 异步 .-> Collector[TokenHub.Collector]
    Collector --> Kafka[Kafka]
    Kafka --> Doris[(Doris<br/>SUM 增量字段)]
    OB --> Audit[审计日志]
    OB --> Metrics[运行时指标]
    PT --> Metrics
````

- **左半边（沙箱内）**：现场记账，给运维和单测用。
- **右半边（沙箱外）**：旁路推送，给财务/产品/计费系统用。
- 两者**互不阻塞、互不替代**：推旁路失败不影响沙箱内累计，沙箱内累计失败也不会回滚旁路。

---

## 五、修改/扩展时的几条护栏

1. **不要**在 `RecordUsage` 里 `await` 任何网络/Kafka 调用，否则 LLM 延迟会爆。
2. **不要**把 `PublishTokenUsageEvent` 和 `record` 合并成单一结构。两者 schema 演进节奏不同。
3. **不要**把 `PublishTokenUsageEvent` 移到 `Session.AddTokenUsage` 之前。`session_total_*` 会少算一通。
4. **新增字段**到 `SessionTokenUsageEvent` 时，记得同步更新 `TokenHub.Collector` 的反序列化模型和 Doris 表 DDL。
5. **新增字段**到 `TurnTokenUsageRecord` 时，记得更新 `ProviderUsageTracker.RecordTurn` 重载或加新重载。

---

## 六、一句话总结

`PublishTokenUsageEvent` 是**给沙箱外面的 TokenHub 用的"对账单"**，`TurnTokenUsageRecord` 是**沙箱里面的"流水本"**。两者字段、消费者、生命周期都不同，**并行存在、各司其职**，并不是"用 A 还是用 B"的选择题。选 A 不用 B，或者反过来，都会破坏观测体系的完整性。
