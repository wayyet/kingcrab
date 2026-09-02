# kingcrab 项目 MAF 编排器与 Planner 任务规划功能分析

> 分析日期：2026-07-04
> 分析范围：`src/OpenClaw.Agent`、`src/OpenClaw.Gateway`、`src/OpenClaw.Core`、`src/OpenClaw.SkillKit`
> 结论先行：kingcrab 用 MAF（Microsoft Agent Framework）做的是**单智能体"对话轮次编排"**（LLM ↔ 工具调用循环），外加 A2A 协议暴露和远程 Durable 工作流的客户端接入；项目中**没有**经典意义上的自主任务规划器（Planner），但有三个"带 Plan 字样"的相关模块（PEV 治理、上下文预算规划、技能运行前检查），职责各不相同。

---

## 一、MAF 编排器做了什么

### 1.1 什么是这里的 MAF

MAF 指微软的 **Microsoft Agent Framework**（NuGet 包 `Microsoft.Agents.AI`）。kingcrab 没有自己手写"LLM 调用 → 解析工具调用 → 执行工具 → 回填结果 → 再调 LLM"这个循环，而是把这个循环整体交给 MAF 的 `ChatClientAgent` 来跑。

启用方式（配置驱动）：

- `appsettings.json` 中 `OpenClaw:Runtime:Orchestrator = "maf"`（第 8 行）
- `Program.cs:74` 调用 `AddMicrosoftAgentFramework()` 注册全部 MAF 组件
- 启动时 `AgentRuntimeFactorySelector` 按配置选中 `MafAgentRuntimeFactory`，生产 `MafAgentRuntime`

### 1.2 核心编排流程（每个对话轮次）

核心类是 [MafAgentRuntime.cs](../src/OpenClaw.Agent/MafAgentRuntime.cs)（实现 `IAgentRuntime`），一次 `RunAsync` 的完整编排步骤：

| 步骤 | 做什么 | 谁负责 |
|------|--------|--------|
| 1. 预算闸门 | 检查会话 Token 预算、合约（Contract）Token/运行时预算，超限直接拒绝 | kingcrab 自研 |
| 2. 组装 Agent | 用 `MafAgentFactory` 每轮新建一个 `ChatClientAgent`（系统提示词 + 工具集） | MAF + kingcrab |
| 3. 加载会话状态 | `MafSessionStateStore` 从磁盘 JSON 边车文件恢复 MAF 的 `AgentSession` | kingcrab 自研 |
| 4. 历史管理 | 历史裁剪（Trim）或压缩（Compaction：调 LLM 把旧对话总结成 2-3 句摘要） | kingcrab 自研 |
| 5. 记忆召回注入 | 按用户消息检索记忆笔记，以"不可信参考资料"形式注入消息列表 | kingcrab 自研 |
| 6. 执行编排循环 | `agent.RunAsync(messages, session, options)` —— LLM 决定调哪些工具、循环执行直到产出最终回复 | **MAF 负责** |
| 7. 落盘与记账 | 保存会话状态、写入历史、Token 用量记账（含缓存读写）、合约快照 | kingcrab 自研 |

流式版本 `RunStreamingAsync` 逻辑相同，用有界 `Channel`（容量 256）把 MAF 的流式增量（文本、推理内容）转成 `AgentStreamEvent` 事件流。

### 1.3 三个关键桥接件（kingcrab 与 MAF 的"接缝"）

MAF 只认自己的接口，kingcrab 通过三个适配器把自家能力接进去：

1. **[MafExecutionServiceChatClient.cs](../src/OpenClaw.Agent/MafExecutionServiceChatClient.cs)**（`IChatClient` 适配器）
   MAF 内部要调 LLM 时，实际走的是 kingcrab 的 `ILlmExecutionService`——这样多 Provider 路由、熔断器、指标、Token/缓存记账全部复用网关已有设施，MAF 感知不到底层换了谁。

2. **`MafToolAdapter`（`AITool` 适配器）**
   把 kingcrab 的 `ITool` 包装成 MAF 认识的 `AITool`，实际执行仍走 `OpenClawToolExecutor`——超时、审批（Tool Approval）、Hook、沙箱、治理（Governance）、审计日志都在这层生效。每轮按会话过滤可见工具（`GetToolDeclarations`），MCP 工具支持热插拔（`ApplyMcpToolChangesAsync`）。

3. **`MafExecutionContextScope`（AsyncLocal 上下文）**
   因为 MAF 的循环是黑盒，kingcrab 用 AsyncLocal 作用域把 Session、TurnContext、审批回调、Token 观察者"偷带"进 MAF 循环内部，工具执行和 LLM 调用时再取出来用。

### 1.4 编排之外的 MAF 相关能力

- **A2A（Agent-to-Agent）协议服务端**：[OpenClawA2AAgent.cs](../src/OpenClaw.Agent/A2A/OpenClawA2AAgent.cs) 继承 MAF 的 `AIAgent`，把整个网关暴露为 `/a2a` 端点（含 Agent Card、可配置的 A2ASkills），供其他智能体调用。
- **远程 Durable 工作流客户端**：[AgentWorkflowRegistry.cs](../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs) + [MafDurableHttpWorkflowRunner.cs](../src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs)。注意：**多步骤持久化工作流不在 kingcrab 进程内跑**，而是通过 HTTP 调用外部 MAF Durable 工作流服务（`run` / `status` / `respond` 三个端点 + 轮询转事件流）。kingcrab 只当注册表和客户端。
- **系统事件编排**：定时任务（cron）触发时，事件文本注入系统提示词而非用户消息，让助手"主动"发消息，历史里看不到用户触发痕迹（`CreateAgentWithSystemEvent`）。
- **技能（Skills）渐进披露**：系统提示词只放技能元数据索引，完整 SKILL.md 通过 `load_skill` 工具按需加载；支持投影合约（Projection Contracts）按请求内容动态放行/封锁技能路由。
- **视觉与多媒体**：`[IMAGE_URL:]` / `[IMAGE_PATH:]` 标记解析为原生 `ImageContent`；非视觉模型则把 base64 图片降级为临时文件路径，交给 `image_analyze` 工具处理。

一句话总结：**MAF 在 kingcrab 里扮演"单智能体 ReAct 循环引擎 + A2A 协议栈"，而记忆、预算、审批、治理、遥测这些"外骨骼"全是 kingcrab 自研并通过适配器挂上去的。**

---

## 二、kingcrab 有没有 Planner 任务规划模块

**结论：没有经典意义的 Planner。** 项目中不存在"接到目标 → LLM 自动拆解成多步计划 → 逐步执行计划"的自主任务规划器（类似 Semantic Kernel Planner 或 AutoGPT 那种）。任务如何拆步完全由 LLM 在 MAF 工具循环里隐式决定。

但有三个名字带 "Plan" 的模块，容易误认，实际职责如下：

### 2.1 PlanExecuteVerifyService（PEV）—— 是"治理闸门"，不是规划器

[PlanExecuteVerifyService.cs](../src/OpenClaw.Gateway/PlanExecuteVerifyService.cs)，模型定义在 [PlanExecuteVerifyModels.cs](../src/OpenClaw.Core/Models/PlanExecuteVerifyModels.cs)。

- 触发条件：工具命中风险类别（`high_risk_tools`、`write_tools`、`shell`、`browser`、`external_api`、`multi_tool_workflows`）
- 触发后：创建 Harness 合约（Contract）+ 证据包（Evidence Bundle），高风险要求人工审批
- 执行后：跑 5 个校验器——工具结果、审批合规、合约完整性、安全姿态、回归检查
- 状态机：`contract_created → awaiting_approval → executing → verifying → verified / failed / rolled_back`

它管的是"**危险动作先立约、后验证**"，属于可靠性/治理机制。名字里的 Plan 指"执行前声明计划"，不是自动生成计划。

### 2.2 ContextBudgetPlanner —— 上下文预算规划，不是任务规划

[ContextBudgetPlanner.cs](../src/OpenClaw.Core/Memory/ContextBudgetPlanner.cs)：为分形记忆（Fractal Memory）挑选最合适的记忆节点导出为上下文块，并按字符/Token 预算截断。规划的对象是"上下文窗口怎么花"，不是"任务怎么拆"。

### 2.3 SkillRunPlanner —— 技能运行前置检查，几乎不算规划

[SkillRunPlanner.cs](../src/OpenClaw.SkillKit/SkillRunPlanner.cs)：只有 20 行，检查技能包输入文件是否存在，生成一份 `SkillRunPlan`（清单 + 输入问题列表）。本质是运行前校验。

### 2.4 沾边但不是 Planner 的机制

- **Handoff 工作流**（`KingcrabHandoffModels.cs`，配置 `Handoff:Workflows:employment-coach`，Kind = `handoff_todo`）：带阶段（material / skill / external / cross_stage）和状态（drafting / ready_to_dispatch）的待办交接流，属于**任务跟踪/流转**，步骤是配置死的，不是动态规划。
- **远程 MAF Durable 工作流**（见 1.4）：真正的多步编排在外部服务，kingcrab 不生成计划。

### 2.5 如果需要 Planner，从哪里长出来

现有架构里最自然的挂点有两个：一是把 PEV 的"合约 + 验证计划"从单工具粒度扩展到多步任务粒度；二是在 `AgentWorkflowRegistry` 里新增一种进程内 backend kind，用 MAF 的 Workflow 能力在本地编排多 Agent 步骤。目前两者都未实现。

---

## 三、关键文件索引

| 文件 | 职责 |
|------|------|
| `src/OpenClaw.Agent/MafAgentRuntime.cs` | MAF 编排运行时主体（轮次编排、历史、记忆、预算） |
| `src/OpenClaw.Agent/MafAgentFactory.cs` | 每轮创建 `ChatClientAgent` |
| `src/OpenClaw.Agent/MafExecutionServiceChatClient.cs` | MAF → kingcrab LLM 执行服务的桥 |
| `src/OpenClaw.Agent/MafSessionStateStore.cs` | MAF 会话状态 JSON 边车持久化（带 schema 版本迁移） |
| `src/OpenClaw.Agent/MafServiceCollectionExtensions.cs` | DI 注册与配置解析（含旧配置节兼容） |
| `src/OpenClaw.Agent/A2A/OpenClawA2AAgent.cs` | A2A 协议宿主 Agent |
| `src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs` | 外部工作流后端注册表 |
| `src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs` | 远程 MAF Durable 工作流 HTTP 客户端 |
| `src/OpenClaw.Gateway/PlanExecuteVerifyService.cs` | PEV 治理编排器（非任务规划器） |
| `src/OpenClaw.Core/Models/PlanExecuteVerifyModels.cs` | PEV 状态机与模型 |
| `src/OpenClaw.Core/Memory/ContextBudgetPlanner.cs` | 上下文预算规划（记忆注入） |
| `src/OpenClaw.SkillKit/SkillRunPlanner.cs` | 技能运行输入校验 |
