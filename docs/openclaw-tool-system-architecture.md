# OpenClaw 工具系统架构：原生工具、预设与执行后端

OpenClaw.NET 的工具系统是其 Agent 能力的核心支柱。从最底层的 `ITool` 接口定义，到工具的分层预设控制，再到跨本地进程、Docker 容器、SSH 远程主机乃至 OpenSandbox 隔离服务的执行路由，整个系统被设计为高度可配置、可扩展且对 AOT 编译友好。本文将四篇文章的内容整合为一篇系统性的架构解析。

---

## 一、核心接口层级

所有原生工具均派生自 **ITool**，这是为了兼容 Native AOT trimmer 而刻意保持极简的基础契约。该接口仅要求实现四个成员：

| 成员 | 类型 | 用途 |
|------|------|------|
| `Name` | `string` | 用于路由、审批策略和日志记录的唯一工具标识符 |
| `Description` | `string` | 在函数架构中发送给 LLM 的自然语言描述 |
| `ParameterSchema` | `string` | 描述可接受参数的原始 JSON Schema |
| `ExecuteAsync` | `ValueTask<string>` | 调用工具并返回结果为纯字符串 |

`ITool` 接口使用 `string`（而非强类型参数对象）来处理参数和结果——这是有意为之的设计：它能保持契约的 AOT 安全性，避免重度依赖反射的序列化，并确保工具层与 `Microsoft.Extensions.AI` 的 `FunctionCallContent` 类型系统保持解耦。

### 扩展接口

三个可选的能力接口扩展了基础契约，工具可以实现其中任意组合：

- **IToolWithContext**：接收包含活跃 `Session` 和 `TurnContext` 的 `ToolExecutionContext`，使工具能够获取会话身份或关联元数据。

- **IStreamingTool**：通过 `IAsyncEnumerable<T>` 产生增量输出，由流式处理流水线消费，从而向客户端提供实时的部分结果。

- **ISandboxCapableTool**：声明了 `DefaultSandboxMode` 并提供了 `CreateSandboxRequest` / `FormatSandboxResult` 方法，允许执行器将工具的工作路由到隔离的执行后端，而不是在本地运行。

---

## 二、工具注册与声明

`NativeAgentRuntimeFactory` 负责在启动时组装工具集。它接收包含来自依赖注入中已解析的 `IReadOnlyList<ITool>` 的 `AgentRuntimeFactoryContext`，并将其传递给 `OpenClawToolExecutor`。如果通过 `DelegationConfig` 启用了委托，工厂会在深度为零处追加一个 `DelegateTool`，从而创建递归的子 Agent 能力。

在 `OpenClawToolExecutor` 内部，每个 `ITool` 都会通过 `CreateDeclaration` 转换为 `AIFunctionDeclaration`（即 `Microsoft.Extensions.AI` 的架构类型），该方法会解析工具原始的 `ParameterSchema` JSON 字符串，并将其封装为类型化的函数声明。这些声明就是 LLM 在 Agent 循环中作为可用函数接收到的内容。

**新增工具的流程极为简洁**：只需实现 `ITool`（可选实现 `ISandboxCapableTool`），并在 DI 容器中使用与 `tool.Name` 匹配的键进行注册，它便会自动出现在 Agent 的工具声明中——无需任何插件清单或桥接配置。

---

## 三、执行流水线

当 Agent 循环产生 `FunctionCallContent` 时，`OpenClawToolExecutor.ExecuteAsync` 会在工具实际运行前编排一个多阶段流水线。这些阶段按严格的顺序执行：

### 1. 预设与路由过滤

在任何钩子或执行逻辑之前，执行器会检查当前会话是否允许使用该工具。这里适用两种机制：

- **工具预设**（通过 `IToolPresetResolver` 解析）基于会话属性限制可用工具集
- **路由级白名单**（`session.RouteAllowedTools`）通过显式包含进行过滤

`IsToolAllowedForSession` 方法在遇到首个匹配的限制条件时会立即短路，从而确保会话无法访问其配置范围外的工具。

### 2. 钩子流水线

工具钩子实现 `IToolHook`（或其增强变体 `IToolHookWithContext`）。执行器按顺序遍历所有已注册的钩子。每次 `BeforeExecuteAsync` 调用都可以通过返回 `false` 来否决执行，这将立即向 LLM 返回拒绝消息。

具备上下文感知能力的变体会接收一个 `ToolHookContext` 结构体，其中包含 `SessionId`、`ChannelId`、`SenderId`、`CorrelationId`、`ToolName`、`ArgumentsJson` 和 `IsStreaming`。执行完成后——无论成功还是失败——`AfterExecuteAsync` 钩子会携带结果文本、耗时和失败标志被触发。

### 3. 动作感知审批

`ToolActionPolicyResolver` 将每次工具调用分类为**变更操作**或**只读操作**。一组硬编码的工具（如 `write_file`、`edit_file`、`shell`、`code_exec`、`git`、`database` 等）始终被视为变更操作。对于诸如 `process` 和 `automation` 这类动作感知型工具，解析器会解析 `argumentsJson` 来检查 `action` 参数——“start”、“write”、“kill” 属于变更操作；而 “log”、“poll”、“wait” 则不属于。

执行器结合三个审批来源来确定是否需要人工确认：
1. 显式配置的 `ApprovalRequiredTools`
2. 预设级审批列表
3. 动作感知的变更检测

优先级逻辑为：显式配置会覆盖动作感知的推断结果。

---

## 四、工具预设与分组系统

OpenClaw.NET 的工具系统向 agent 暴露了数十个原生及插件提供的工具，但并非每个渠道或会话都应访问所有工具。工具预设**定义了命名后的允许工具集、自主级别和审批要求；**工具集**则是预设组合的可复用构建块。表面绑定将渠道类型自动映射到预设。这三层结构共同使操作员能够对 agent 在每个界面上可执行的操作进行细粒度控制。

### 架构概述

预设解析管道流经三个阶段：**表面推断**确定会话到达的渠道，**预设查找**为该表面寻找匹配的预设，**规则应用**通过将工具集和直接规则与完整工具注册表进行交集和差集运算，计算出最终允许的工具集。

### 配置层

**工具集**是由字符串键标识的允许/拒绝规则的命名集合。工具集使得多个预设可以共享公共工具子集而无需重复。`ToolsetConfig` 模型支持四种规则类型：

| 规则属性 | 行为 | 匹配语义 |
|---------|------|---------|
| `AllowTools` | 将精确匹配的工具名称添加到允许集 | 不区分大小写的精确匹配 |
| `AllowPrefixes` | 添加名称以该前缀开头的所有工具 | 不区分大小写的 `StartsWith` |
| `DenyTools` | 从允许集中移除精确匹配的工具名称 | 不区分大小写的精确匹配 |
| `DenyPrefixes` | 从允许集中移除所有匹配的工具 | 不区分大小写的 `StartsWith` |

**内置工具集**以 `group:` 为前缀，始终可用：

| 内置工具集键 | 包含的工具 |
|-------------|-----------|
| `group:runtime` | `shell`, `process`, `code_exec` |
| `group:fs` | `read_file`, `write_file`, `edit_file`, `apply_patch` |
| `group:sessions` | `sessions`, `sessions_history`, `sessions_send`, `sessions_spawn`, `session_status`, `session_search`, `agents_list` |
| `group:memory` | `memory`, `memory_search`, `memory_get`, `project_memory` |
| `group:web` | `web_search`, `web_fetch`, `x_search`, `browser` |
| `group:automation` | `cron`, `automation`, `gateway`, `todo` |
| `group:messaging` | `message` |

**预设**是生成 `ResolvedToolPreset` 的顶层配置对象。除了工具过滤之外，预设还控制两个关键的行为开关：

- **AutonomyMode**：覆盖全局 `Tooling:AutonomyMode`，取值 `readonly`、`supervised`、`full`
- **RequireToolApproval**：覆盖全局 `Tooling:RequireToolApproval`

### 内置预设

系统开箱即用地提供了九个内置预设：

| 预设 ID | 策略 | 被阻止的变更工具 | 需要审批 | 自主性 |
|--------|------|----------------|---------|--------|
| `full` | 所有已注册的工具 | 无 | 全局默认 | 全局默认 |
| `cli` | openai-http 的默认设置 | 无 | 全局默认 | 全局默认 |
| `coding` | 交集白名单 | 不在白名单中的所有工具 | 全局默认 | 全局默认 |
| `messaging` | 交集白名单 | 不在白名单中的所有工具 | 全局默认 | 全局默认 |
| `minimal` | 仅 `session_status` | 除 `session_status` 外的所有工具 | 全局默认 | 全局默认 |
| `web` | 从完整集移除危险工具 | `shell`, `process`, `write_file`, `code_exec`, `git`, `automation` | `process`, `automation` | 全局默认 |
| `telegram` | 移除危险工具 + 浏览器 + 委派 | `DefaultWebDeny` + `browser`, `delegate_agent` | `process`, `automation` | 全局默认 |
| `automation` | 最小化 + 自动化工具 | `shell`, `write_file`, `code_exec`, `git`, `browser` | `automation` | 全局默认 |
| `readonly` | 剥离所有可写工具 | 11 个工具 | 全局默认 | 强制 `readonly` |

`web` 和 `telegram` 预设即使在全局设置为 `false` 时也会强制 `RequireToolApproval = true`——这是一种刻意的安全边界，面向公众的渠道始终将危险工具拦截在用户确认之后。

### 表面绑定

表面绑定将渠道标识符映射到预设 ID。如果没有绑定，解析器将应用硬编码的默认值：

| 渠道 ID 模式 | 默认表面 | 默认预设 |
|-------------|---------|---------|
| `openai-http` | `cli` | `cli` |
| `websocket` | `web` | `web` |
| 包含 `telegram` | `telegram` | `telegram` |
| `cron` 或会话 ID 以 `automation:` 开头 | `automation` | `automation` |
| 其他任何情况 | 原始渠道 ID | 回退到 `cli` |

---

## 五、工具执行后端

OpenClaw.NET 并非以相同方式执行所有工具。由配置驱动的执行路由层会决定工具调用是直接在网关进程中运行、在 Docker 容器内运行、在远程 SSH 主机上运行，还是通过 OpenSandbox 隔离服务运行。

### 调度架构

当 Agent 选择一个工具时，`OpenClawToolExecutor.ExecuteToolWithRoutingAsync` 会调用 `ToolExecutionRouter.TryResolveRoute` 来判断该工具是否有显式路由，或者是否符合通过 `ISandboxCapableTool` 进行的旧版沙箱路由条件。

### 后端类型

每个后端都实现由 `IExecutionBackend` 定义的相同最小契约，从而保持较小的表面积以利于 AOT 编译。

| 后端 | 类 | 适用场景 |
|------|-----|---------|
| `Local` | `LocalExecutionBackend` | 宿主机上的进程内执行 |
| `Docker` | `DockerExecutionBackend` | 带有资源限制的容器隔离执行 |
| `Ssh` | `SshExecutionBackend` | 在配置的 SSH 主机上远程执行 |
| `OpenSandbox` | `OpenSandboxExecutionBackend` | OpenClaw 的内置沙箱服务 |

### 能力矩阵

| 能力 | Local | Docker | SSH | OpenSandbox |
|------|-------|--------|-----|-------------|
| 单次命令 | ✅ | ✅ | ✅ | ✅ |
| 后台进程 | ✅ | ✅ | ✅ | ❌ |
| PTY 支持 | ✅ (非 Windows) | ❌ | ❌ | ❌ |
| 交互式输入 | ✅ | ✅ | ✅ | ❌ |

### 路由配置

两条配置路径决定了工具的执行位置：

**ExecutionConfig（主要机制）**：定义在 `GatewayConfig.Execution` 处。当没有匹配的单工具路由时，将使用 `DefaultBackend`（默认为 `"local"`）。`Profiles` 字典按名称声明可用的后端。`Tools` 字典将工具名称映射到 `ExecutionToolRouteConfig` 条目。

**SandboxConfig（旧版）**：定义在 `GatewayConfig.Sandbox` 处。控制实现 `ISandboxCapableTool` 的工具的沙箱行为。存在三种模式：

| 属性 | None | Prefer | Require |
|------|------|--------|---------|
| 沙箱可用 | 本地执行 | 沙箱执行 | 沙箱执行 |
| 沙箱不可用 | 本地执行 | 回退到本地 | 失败关闭 |

### 回退与错误语义

`ToolExecutionRouter.ExecuteAsync` 方法将后端调用包装在 try/catch 块中，当主后端抛出异常时会尝试使用 `FallbackBackend`。如果主后端成功，结果中的 `FallbackUsed` 为 `false`。如果触发了回退，整个 `ExecutionRequest` 将使用回退后端名称重新构造并再次执行。

---

## 六、支持沙箱的工具

实现 `ISandboxCapableTool` 的工具会声明如何将其工作打包以供远程执行：

- **ShellTool**：声明了 `DefaultSandboxMode.Prefer`，并将用户命令封装为 `/bin/sh -lc <command>`
- **CodeExecTool**：同样声明了 `Prefer`，并根据 `language` 参数解析相应的解释器（python3、node、bash）

### 执行后端路由

`ToolExecutionRouter` 负责解析应由哪个后端实际执行支持沙箱的工具任务。路由器的 `TryResolveRoute` 方法遵循优先级链：

1. 首先检查 `Execution.Tools` 中是否存在显式的按工具映射的后端
2. 如果不存在且该工具支持沙箱，它会根据全局沙箱策略评估工具的 `DefaultSandboxMode`
3. 如果配置了 OpenSandbox 提供程序，它会创建一个指向 `opensandbox` 后端的旧版沙箱路由

---

## 七、原生工具目录

`src/OpenClaw.Agent/Tools/` 目录包含了完整的原生工具集，按功能类别组织：

### 文件系统工具

| 工具 | 核心行为 |
|------|---------|
| `read_file` | 有界行读取（最多 5,000 行），通过 `ToolPathPolicy.IsReadAllowed` 强制执行路径策略 |
| `write_file` | 通过临时文件 + 重命名实现原子写入；受 `ReadOnlyMode` 阻止 |
| `edit_file` | 针对性的行编辑 |
| `apply_patch` | 统一差异应用 |
| `pdf_read` | PDF 文本提取 |

### 执行工具

| 工具 | DefaultSandboxMode | 后端 |
|------|-------------------|------|
| `shell` | Prefer | 本地进程、Docker、SSH、OpenSandbox |
| `code_exec` | Prefer | Docker（隔离、`--network=none`、`--memory=256m`）、本地进程 |
| `process` | — | 带有动作感知审批的后台进程管理 |

### 集成与外部服务工具

`web_search`、`web_fetch`、`x_search`、`home_assistant`、`mqtt`、`notion`、`email`、`calendar`、`inbox_zero`、`image_gen`、`browser`、`database`、`git` 等。

### 记忆与会话工具

`memory_search`、`memory_note`、`memory_get`、`project_memory`、`sessions`。

### 委托

`DelegateTool` 会递归地创建具有特定角色的子 Agent 运行时。每次委托都会生成一个带有专用 `AgentRuntime` 的临时会话，并可选择将其限制在配置文件 `AllowedTools` 指定的工具子集内。深度计数器可防止无限递归。

### MCP 原生工具

`McpNativeTool` 将 MCP（Model Context Protocol）服务器工具封装为原生的 `ITool` 实现，从而将外部的 MCP 生态系统桥接到同一执行流水线中。

---

## 八、总结：分层工具访问控制

工具预设系统的设计理念是**增量锁定**：在开发期间从 `full` 访问权限开始，然后在渠道暴露给信任度较低的表面时逐步限制它们。可组合的工具集和预设模型意味着你可以定义一个可复用的访问配置文件库，并针对每个渠道进行混合使用而无需重复。

整个工具系统的分层防御体现在：

1. **声明时的预设过滤**：LLM 永远看不到其预设之外的工具
2. **执行时的变更检测**：预设范围内的变更性调用面临审批拦截
3. **后端级的沙箱路由**：高风险工具被路由到隔离环境执行

这种纵深防御确保了即使某一层被突破，其他层仍能提供保护。
