# 迁移排除清单（exclusion-list）

本文档沉淀 openclaw.net → kingcrab 增量迁移过程中**永久不迁入**或**已删除**的文件与原因，作为后续 Task C/D 检查的权威依据。

## 1. 上游独立项目（整项目跳过）

下列项目的所有源代码、csproj 与 sln 注册不进入 kingcrab：

| 项目 | 跳过原因 |
| ---- | -------- |
| `OpenClaw.MicrosoftAgentFrameworkAdapter` | kingcrab 已将 MAF 类型内联到 `OpenClaw.Agent`（`Maf*.cs`），无需独立程序集 |
| `OpenClaw.SemanticKernelAdapter` | MAF-only 路线不再保留 SK 通路 |
| `OpenClaw.Providers.MicrosoftExtensionsAI` | 不再注册 `Microsoft.Extensions.AI` 桥接提供者 |
| `OpenClaw.Embeddings.Onnx` | 不引入 Onnx 本地 Embedding |
| `whatsapp-baileys-worker` (Node.js) | 非 .NET 项目，落在外部仓库 |
| `whatsapp-whatsmeow-worker` (Go) | 非 .NET 项目，落在外部仓库 |

> 例外：`OpenClaw.Plugins.Mempalace` 已于 Task C-0 作为新独立项目迁入 kingcrab。

## 1.1 上游第三方调度依赖（功能整体跳过）

| 依赖 / 文件 | 跳过原因 |
| ----------- | -------- |
| NuGet `TickerQ` (10.3.0) | kingcrab 不引入第三方调度框架 |
| `using TickerQ.DependencyInjection;` / `services.AddTickerQ();` / `app.UseTickerQ();` | 一并移除（`OpenClaw.Gateway/Program.cs`、`Composition/CoreServicesExtensions.cs`） |
| `src/OpenClaw.Gateway/Pipeline/CronSchedulerTickerFunction.cs` | TickerQ `[TickerFunction]` 钩子，删除 |
| `src/OpenClaw.Gateway/Pipeline/CronSchedulerStartupService.cs` | 与 TickerQ 链路同属调度宿主，整体不迁入；`CronScheduler` 类本身保留（基于 NCrontab，可作为 cron 表达式工具/状态查询使用） |
| `using TickerQ.Utilities;` / `CronExpression.TryParse` (Core/Pipeline/CronScheduler.cs) | 改用 NCrontab 原生 `CrontabSchedule.Parse` 替代 |

## 2. 共有项目内的 Native runtime 文件（跳过）

| 文件 | 跳过原因 |
| ---- | -------- |
| `src/OpenClaw.Agent/AgentRuntime.cs` | Native runtime，MAF-only 路线不启用 |
| `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs` | 同上 |
| `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.MafConfigNotices.cs` | 既有跳过项（沿用 kingcrab 现状） |

## 3. 已删除的孤儿测试（Task B）

以下文件存在于 kingcrab `src/OpenClaw.Tests/` 但引用已排除命名空间，按 plan §4 删除：

| 文件 | 引用的排除命名空间 |
| ---- | ------------------ |
| `SemanticKernelInteropTests.cs` | `OpenClaw.SemanticKernelAdapter`、`Microsoft.SemanticKernel` |
| `A2AIntegrationTests.cs` | `OpenClaw.MicrosoftAgentFrameworkAdapter`、`OpenClaw.MicrosoftAgentFrameworkAdapter.A2A`（即使 `#if OPENCLAW_ENABLE_MAF_EXPERIMENT` 包裹也删除，避免命名空间漂移） |
| `MafGatewayIntegrationTests.cs` | 同上（`#if OPENCLAW_ENABLE_MAF_EXPERIMENT`） |

> 上游 `OpenClaw.Tests/A2AHttpEndpointTests.cs`、`MicrosoftExtensionsAiProviderBridgeTests.cs`、`OpenClaw.TestPluginFixtures/MicrosoftExtensionsAiProviderFixtures.cs` 在 kingcrab 端**不存在**（未复制），保持现状不迁入。

## 4. Gateway 运行时数据/示例（部分跳过）

`src/OpenClaw.Gateway/memory/`、`src/OpenClaw.Gateway/skills/` 下的运行时数据与样例文件**不同步**，仅业务示例的 `SKILL.md` 可保留：

- 保留：`skills/homeassistant-operator/SKILL.md`、`skills/mqtt-operator/SKILL.md`
- 跳过：上述目录下其它运行时产物（数据库、临时文件、对话样本等）

## 5. kingcrab 独有内容（不会被覆盖）

下列在 Task C/D 内容合并阶段保持 kingcrab 现状：

- `Kingcrab.AppHost/`、`Kingcrab.ServiceDefaults/`（.NET Aspire 编排，与上游无对应）
- `OpenClaw.Plugins.EmploymentCoachWorkflow/`、`OpenClaw.SandboxDemo/`（kingcrab 独有插件/演示）
- `OpenClaw.Agent/Maf*.cs`（内联自上游 `OpenClaw.MicrosoftAgentFrameworkAdapter`）
- `OpenClaw.Core/Skills/SkillProjectionResolver.cs`、`OpenClaw.Core/Pipeline/SessionAbortRegistry.cs` 等 6 个 Core 独有文件
- `OpenClaw.Tests` 5 个 kingcrab 独有用例：`CronSchedulerTests.cs`、`GatewayAdminEndpointSourceTests.cs`、`HandoffToolTests.cs`、`MafAgentRuntimeTests.cs`、`MafTestRuntimeFactory.cs`

## 6. 命名空间重映射（Task D 内容合并阶段执行）

| 上游引用 | kingcrab 替换 | 备注 |
| -------- | ------------- | ---- |
| `using OpenClaw.MicrosoftAgentFrameworkAdapter;` | `using OpenClaw.Agent;` | Maf 类型已内联 |
| `using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;` | `using OpenClaw.Agent.A2A;`（如不存在则删除该测试） | 视实际内联范围决定 |
| 注册 `OpenClaw.Providers.MicrosoftExtensionsAI` 服务 | 删除该 DI 注册（或 `#if KINGCRAB_NATIVE_AI_DISABLED` 排除） | 不引入 MEAI 提供者 |
| 注册 `OpenClaw.SemanticKernelAdapter` 服务 | 删除该 DI 注册 | 不引入 SK 通路 |

---

更新时间：Task B 完成后初次创建。后续 Task C/D 如有新增排除项请追加至对应章节。
