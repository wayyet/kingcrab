# OpenClaw Dispatch Control Plane

> 本文记录 `employment-coach-conversation` 到 `ontology-extraction` / `skill-generation` / `external-config` 的 dispatch 方案，以及当前 Gateway 侧实现。重点是：模型只发控制信号，宿主负责拦截、校验、落库、调度和回调合流。

## 背景

Employment Coach 工作流里，主 skill 需要把阶段性 Handoff todo 交给下游 skill 处理。例如资料阶段交给 `ontology-extraction`，技能阶段交给 `skill-generation`，外部配置阶段交给 `external-config`。

这个交接不能直接依赖模型在自然语言里说“我去处理了”，也不能让模型自己伪造调度状态。原因有三点：

1. Handoff todo 的状态必须由宿主按真实接受结果推进。
2. `<dispatch>` / `<dispatch_callback>` 是控制面事件，不应该泄漏给用户或持久化成可见对话文本。
3. 流式输出时，控制块可能被拆成多个 chunk，必须在 Gateway 边界过滤。

因此实现采用 Gateway 侧 control plane：assistant 输出控制块，Gateway 拦截并解析，校验会话 Handoff metadata，生成真实 `dispatch_id`，再启动下游子会话并把回调作为 system event 注入父会话。

## 协议

### 发起 dispatch

主 skill 在 assistant 输出中嵌入 `<dispatch>` 控制块：

```json
<dispatch>{
  "target": "ontology-extraction",
  "handoff_ids": ["m_cs_nonstandard_rules_001"],
  "mode": "incremental",
  "note": "用户表示这批资料先这些"
}</dispatch>
```

字段含义：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `target` | 是 | 下游目标：`ontology-extraction`、`skill-generation`、`external-config`、`stage_transition` |
| `handoff_ids` | 对非 `stage_transition` 必填 | 本次调度覆盖的 Handoff todo id 列表 |
| `mode` | 否 | 下游模式，例如 `incremental` / `full_replace` |
| `note` | 否 | 给宿主或下游的简短上下文 |
| `to` | `stage_transition` 必填 | 阶段跳转目标 |

兼容读取单数 `handoff_id`，但规范输出应使用 `handoff_ids`。

### 下游 callback

下游 skill 完成后输出 `<dispatch_callback>`：

```json
<dispatch_callback>{
  "source_dispatch_target": "ontology-extraction",
  "handoff_ids": ["m_cs_nonstandard_rules_001"],
  "user_summary": "已从资料中抽出退货判定条件、处置档位和人工分流触发节点。",
  "todo_results": [
    {
      "handoff_id": "m_cs_nonstandard_rules_001",
      "status": "success",
      "artifacts": ["ontology/return-policy.slice.json"],
      "errors": []
    }
  ],
  "status": "success",
  "errors": []
}</dispatch_callback>
```

`todo_results` 必须精确覆盖 `handoff_ids`。callback 只记录结果和摘要，不自动把 Handoff todo 变成 `confirmed`；最终确认仍由用户确认后主 skill 调用 Handoff tool 完成。

## 组件

当前实现集中在 Gateway 的 `Dispatch` 命名空间：

| 组件 | 文件 | 职责 |
| --- | --- | --- |
| `ControlBlockExtractor` | `src/OpenClaw.Gateway/Dispatch/ControlBlockExtraction.cs` | 从普通文本或流式 chunk 中提取 `<dispatch>` / `<dispatch_callback>`，返回可见文本和控制块 |
| `DispatchSignalParser` | `src/OpenClaw.Gateway/Dispatch/ControlBlockExtraction.cs` | 把控制块 JSON 转成 `DispatchSignal` / `DispatchCallbackSignal` |
| `WorkflowDispatchCoordinator` | `src/OpenClaw.Gateway/Dispatch/WorkflowDispatchCoordinator.cs` | 校验 Handoff 状态，接受或拒绝 dispatch/callback，更新 metadata |
| `DispatchInterceptingAgentRuntime` | `src/OpenClaw.Gateway/Dispatch/DispatchInterceptingAgentRuntime.cs` | 包装 `IAgentRuntime`，拦截 assistant 输出和 system-event callback 输入 |
| `WorkflowDispatchRunner` | `src/OpenClaw.Gateway/Dispatch/WorkflowDispatchRunner.cs` | 后台启动下游子会话，提取 callback，注入父会话 |
| `SessionDispatchItem` | `src/OpenClaw.Core/Models/OperatorApiModels.cs` | 持久化 dispatch 记录 |
| `SessionMetadataStore` | `src/OpenClaw.Gateway/SessionMetadataStore.cs` | 归一化并保存 Handoff todo 和 dispatch item |

运行时接线位于 `RuntimeInitializationExtensions.InitializeOpenClawRuntimeAsync`：

```text
inner IAgentRuntime
  -> WorkflowDispatchRunner(inner runtime, SessionManager, MessagePipeline)
  -> WorkflowDispatchCoordinator(metadata store, runner)
  -> DispatchInterceptingAgentRuntime(inner runtime, coordinator)
  -> Gateway runtime
```

runner 使用 wrapper 之前的 inner runtime 运行子会话，避免子会话输出的 `<dispatch_callback>` 被本地 wrapper 提前吞掉。父会话收到 callback 时，再由 wrapper 统一处理。

## 数据模型

`SessionMetadataSnapshot` 新增 `DispatchItems`：

```json
{
  "dispatch_id": "dispatch_20260511123000_abcd1234",
  "session_id": "websocket:user",
  "source_skill": "employment-coach-conversation",
  "target": "ontology-extraction",
  "handoff_ids": ["m_cs_nonstandard_rules_001"],
  "mode": "incremental",
  "note": "用户表示这批资料先这些",
  "to": null,
  "status": "accepted",
  "created_at": "2026-05-11T12:30:00Z",
  "updated_at": "2026-05-11T12:30:00Z",
  "completed_at": null,
  "callback_summary": null,
  "errors": []
}
```

常见 `status`：

| 状态 | 含义 |
| --- | --- |
| `accepted` | 宿主已接受 dispatch，并生成真实 `dispatch_id` |
| `success` | 下游 callback 全部成功 |
| `partial` | 下游 callback 部分成功或有 warning |
| `failed` | 下游 callback 全部失败，或 runner 合成失败 callback |
| `stale` | callback 到达时对应 Handoff todo 已被用户改成 `dirty`，旧结果不能直接确认 |

## Dispatch 流程

```text
assistant output
  -> DispatchInterceptingAgentRuntime
  -> ControlBlockExtractor removes control blocks from visible text
  -> WorkflowDispatchCoordinator validates Handoff metadata
  -> SessionMetadataStore marks Handoff todo as dispatched and records SessionDispatchItem
  -> WorkflowDispatchRunner starts child dispatch session
  -> downstream skill returns dispatch_callback
  -> runner injects callback as parent system event
  -> parent runtime processes callback metadata
  -> parent assistant summarizes result for user and asks confirmation
```

### 非流式路径

`RunAsync` 的处理顺序：

1. 调用 inner runtime 得到完整 assistant 文本。
2. 调用 `ControlBlockExtractor.Extract`。
3. 如果没有控制块，原样返回。
4. 如果有控制块，用可见文本替换最后一条 assistant history。
5. 调用 coordinator 处理控制块。
6. 只把可见文本返回给 Gateway 输出。

### 流式路径

`RunStreamingAsync` 使用 `StreamingControlBlockFilter`：

1. 每个 `TextDelta` 先进入 filter。
2. 普通文本立即继续向客户端输出。
3. 可能是控制标签前缀的尾部会被暂存，避免 `<dis` / `patch>` 这种跨 chunk 标签泄漏。
4. 完整控制块被吞掉并交给 coordinator。
5. `Done` 到达时 flush 剩余可见文本，并用累计可见文本替换最后一条 assistant history。

filter 有 64 KiB 控制块上限。超过上限或 flush 时仍不闭合的控制块会被丢弃，不作为可见文本输出。

## 接受 dispatch 的校验规则

`WorkflowDispatchCoordinator.AcceptDispatch` 是唯一能接受 dispatch 的入口。

### target 映射

| target | stage | target_skill |
| --- | --- | --- |
| `ontology-extraction` | `material` | `ontology-extraction` |
| `skill-generation` | `skill` | `skill-generation` |
| `external-config` | `external` | `external-config` |

`stage_transition` 单独处理，不要求 Handoff todo，但必须有 `to`。

### Handoff 状态规则

对普通 target：

1. `handoff_ids` 必须非空。
2. 每个 id 必须属于当前 session。
3. 每个 item 必须是 `kind=handoff_todo`，且 `stage` / `target_skill` 匹配 target。
4. 只允许 `ready_to_dispatch` 或 `dirty` 被 dispatch。
5. 同 stage/target 下如果存在 `drafting`、`dispatched`、`needs_review`，拒绝本次 dispatch。
6. `handoff_ids` 必须等于当前 stage/target 下所有活跃且可 dispatch 的 id 集合，防止模型只挑一部分绕过未闭环工作。

`material/ontology-extraction` 还要求：

- `category` 非空。
- `payload.objective` 非空。
- `payload.scene_hint` 非空。
- 至少存在 `payload.source_files`、`payload.source_content` 或 `payload.source_summary` 之一。

### 接受后的状态变化

接受成功后：

1. 生成真实 `dispatch_id`。
2. 对选中的 Handoff todo 写入 `dispatch_id`。
3. 状态改为 `dispatched`。
4. `ready_to_dispatch -> dispatched` 时 revision 加 1。
5. `dirty -> ready_to_dispatch -> dispatched` 的逻辑压缩为 revision 加 2。
6. 新增一条 `SessionDispatchItem(status=accepted)`。
7. 如果配置了 runner，则异步入队下游执行。

拒绝时只写日志，不改变 metadata，也不把控制块显示给用户。

## Callback 流程

callback 可来自两种路径：

1. 下游子会话输出 `<dispatch_callback>`，runner 提取后注入父会话。
2. 外部系统直接把 `<dispatch_callback>` 作为 system event 发给父会话。

父会话 wrapper 会先处理 system-event 输入中的控制块，然后把一段干净的 system prompt 交给模型，例如：

```text
A downstream dispatch callback was received:
- ontology-extraction: 已生成入库流程本体切片。
Briefly summarize the result for the user and ask for confirmation. Do not mark Handoff todos as confirmed automatically.
```

这让 assistant 可以自然地向用户复述结果，而不是把原始 JSON 暴露给用户。

### 接受 callback 的校验规则

`AcceptCallback` 会检查：

1. `handoff_ids` 非空。
2. `todo_results[].handoff_id` 精确覆盖 `handoff_ids`。
3. 能找到 target 和 handoff id 集合都匹配的历史 dispatch。

接受成功后：

1. 更新对应 `SessionDispatchItem.status` 为 callback status。
2. 写入 `completed_at`、`callback_summary`、`errors`。
3. 对非 `dirty` Handoff todo 写入 `callback_summary`，保持原状态为 `dispatched`。
4. 如果 callback 到达时某个 Handoff todo 已是 `dirty`，dispatch 记录标为 `stale`，旧 callback 不覆盖该 todo。

注意：callback 不会把 Handoff todo 改成 `confirmed`。确认仍必须由主 skill 在用户明确确认后通过 Handoff tool transition 完成。

## Runner 设计

`WorkflowDispatchRunner` 是 fire-and-forget 后台执行器。coordinator 接受 dispatch 后把 `WorkflowDispatchExecutionRequest` 入队，runner 在后台执行：

1. 创建 child session：`{parentSessionId}:dispatch:{dispatchId}`。
2. 构造 system prompt，包含 dispatch envelope 和完整 `handoff_todos` JSON。
3. 调用 inner `IAgentRuntime.RunAsync(..., isSystemEvent: true)`。
4. 从 child response 提取匹配的 `<dispatch_callback>`。
5. 如果没有合法 callback，合成一个 `failed` callback。
6. 把 callback 作为 parent session 的 `InboundMessage(IsSystem=true)` 写回 `MessagePipeline`。

runner 合成失败 callback 的原则是：每个 handoff id 都有一条 `todo_results`，状态为 `failed`，并把错误放入 `errors`。这样父会话总能用统一 callback 路径合流。

## Stage Transition

`target=stage_transition` 表示主工作流阶段可以进入下一段，例如：

```json
<dispatch>{
  "target": "stage_transition",
  "handoff_ids": [],
  "to": "instance_packaging",
  "note": "三个阶段的必需项均已完成，可进入打包"
}</dispatch>
```

Gateway 当前只校验并记录这类 dispatch：

- `handoff_ids` 必须为空。
- `to` 必须非空。
- 新增 `SessionDispatchItem(status=accepted)`。
- 不启动下游 runner。

## 历史与 UI 清理

控制块是控制面，不是用户内容。因此 wrapper 会做两层清理：

1. 返回给 Gateway 的文本只包含可见文本。
2. `session.History` 中最后一条 assistant turn 会被替换为可见文本。

这保证：

- WebSocket 流不会看到 `<dispatch>` JSON。
- A2A / DevUI / 普通 channel 不会收到控制块。
- 持久化 history 重放时不会暴露内部控制协议。

## 测试覆盖

新增测试位于 `src/OpenClaw.Tests/DispatchInterceptorTests.cs`，覆盖：

- 流式 filter 处理 `<dis` + `patch>` 跨 chunk 标签。
- dispatch parser 提取控制块 JSON。
- coordinator 拒绝 `drafting` Handoff todo。
- coordinator 接受 `ready_to_dispatch` material todo，写入 dispatch 并转为 `dispatched`。
- callback 缺少 `todo_results` 覆盖时拒绝。
- callback 写入 summary，但不自动 confirm。
- `RunAsync` 清理返回文本和 history，并触发 metadata 更新。
- system-event callback 在进入 inner runtime 前被处理和清理。

推荐验证命令：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter DispatchInterceptorTests
```

## 设计边界

当前实现刻意保留以下边界：

1. dispatch 接受和下游执行解耦。coordinator 负责状态正确性，runner 负责异步执行。
2. runner 是 best-effort fire-and-forget，不提供重试、幂等恢复或独立队列表。
3. 下游 skill 必须遵守 callback 合约；如果不遵守，runner 合成失败 callback。
4. callback 不确认 Handoff todo，只让主 skill 面向用户复述并等待确认。
5. `stage_transition` 只是控制面记录，不直接改变 UI stage 或启动打包流程。

后续如果要把 dispatch 做成更强的生产级编排，可以沿三个方向扩展：

- 增加持久化 dispatch queue 和 retry policy。
- 增加 operator UI 查看 dispatch / callback / stale 状态。
- 给 `stage_transition` 接入真正的阶段状态机或打包 runner。
