# 下一次 openclaw.net → kingcrab 迁移 Playbook

> 来源：本仓库 build-iter-1..19 全过程的实战提炼。
> 适用：再次将 [openclaw.net 上游仓库](../../) 的增量代码同步到 kingcrab。
> 配套文件：[exclusion-list.md](./exclusion-list.md)、[migration-summary.md](./migration-summary.md)、[only-in-openclaw-copy-report.md](./only-in-openclaw-copy-report.md)、`build-logs/build-iter-*.log`。

---

## 0. 前置原则（永久生效，不要再问）

| 原则 | 说明 |
| --- | --- |
| **MAF-only** | 只保留 Microsoft Agent Framework 一条 LLM 通路。SK / MEAI / Onnx 桥接项目永远不迁入。 |
| **Native runtime 排除** | `OpenClaw.Agent/AgentRuntime.cs`、`NativeAgentRuntimeFactory.cs`、`RuntimeInitializationExtensions.MafConfigNotices.cs` 一律不迁入。 |
| **TickerQ 整链剔除** | TickerQ 包、`TickerQ.Utilities`、`CronSchedulerTickerFunction`、`CronSchedulerStartupService` 全部不迁入；`CronScheduler` 类保留但只用 NCrontab。 |
| **Aspire 编排不可覆盖** | `Kingcrab.AppHost` / `Kingcrab.ServiceDefaults` 是 kingcrab 独有，不被上游同步覆盖。 |
| **plan 文件只读** | 只更新 todo list 与 docs/migration 下文档，不修改 plan 本体。 |
| **content-sync 是有损的** | 上游覆盖 common 文件会冲掉 kingcrab 独有成员。任何 sync 后必须立刻校验"独有清单"是否还在（详见 §5）。 |

---

## 1. 任务序列（A → G）

| Task | 输入 | 产出 | 通过判据 |
| --- | --- | --- | --- |
| **A 基线刷新** | 上游 + kingcrab 当前 | `docs/migration/diff-*.md`、`only-in-openclaw-copy-report.md` 重新生成 | diff 报告全部更新到当前 HEAD |
| **B 孤儿测试清理** | A 的 diff | 删除引用排除命名空间的测试 | `OpenClaw.Tests` 不引用 SK/MAF Adapter/MEAI |
| **C-0 ~ C-8 增量复制** | only-in-openclaw 清单 | 按项目把 only-in 文件复制进 kingcrab | 各项目无 only-in 文件未处理 |
| **D 共有文件三方合并** | A 的 content-diff | 用上游覆盖 + 后处理（命名空间重映射、kingcrab 独有补回） | content-diff 列示文件全部 sync |
| **E csproj/sln 校准** | C/D 引入的项目 | 包引用、`OpenClaw.Net.slnx` 注册同步 | 无未注册项目，无残留排除项包引用 |
| **F 构建验证（迭代）** | C/D/E 全部完成 | `build-iter-N.log` | `dotnet build OpenClaw.Net.slnx -c Debug` 0 错误 0 警告 |
| **G 交付文档** | F 通过 | 更新 `migration-summary.md`、`exclusion-list.md` | 文档状态与代码一致 |

---

## 2. Task A 基线刷新（标准命令）

```powershell
# 1. 拉上游（假设 remote 名为 upstream）
git fetch upstream main

# 2. 重生成 diff 报告（按项目逐项跑）
#    现成脚本可参考 scripts/migration/iter-fix.ps1 / show-errors.ps1
```

校验输出：每份 `docs/migration/diff-*.md` 的"上次更新时间"应该是当天。

---

## 3. Task B 孤儿测试清理 checklist

逐文件 grep `using OpenClaw.SemanticKernelAdapter|using OpenClaw.MicrosoftAgentFrameworkAdapter|using OpenClaw.Providers.MicrosoftExtensionsAI`，全部删除。

历史已删（保持永久排除）：
- `SemanticKernelInteropTests.cs`
- `A2AIntegrationTests.cs`
- `MafGatewayIntegrationTests.cs`

---

## 4. Task C 增量复制要点

**只复制 only-in-openclaw（上游有 / kingcrab 没有）的文件。**

- 复制后立即手工修正命名空间（`OpenClaw.MicrosoftAgentFrameworkAdapter` → `OpenClaw.Agent`）。
- 凡是 `*RuntimeFactory.cs` / `AgentRuntime*.cs` 类的 native 文件，一律按 [exclusion-list.md §2](./exclusion-list.md) 跳过。
- 历史误判 1 例：`OpenClaw.Agent/Memory/FractalMemoryMcpProvider.cs` 曾被误归类为 native，后又复回。**判断准则：是否依赖 native `AgentRuntime` 类型**——只依赖 `IAgentRuntime` 接口的不算 native。

---

## 5. Task D 三方合并 — 最关键的一步（踩坑高发区）

### 5.1 操作流程

1. 用上游内容**覆盖** kingcrab 共有文件（content-diff 列示者）。
2. 立刻按 [exclusion-list.md §6](./exclusion-list.md) 做命名空间替换。
3. **必查 kingcrab 独有成员是否被冲掉**（见 §5.2 清单）。

### 5.2 历史上被冲掉过的 kingcrab 独有成员（务必逐项验证）

| 文件 | kingcrab 独有成员 | 验证方法 |
| --- | --- | --- |
| `OpenClaw.Agent/IAgentRuntime.cs` | `RunAsync` / `RunStreamingAsync` 的 `bool isSystemEvent = false` 默认参数；`LoadedTools` / `ApplyMcpToolChangesAsync` 默认成员 | grep `isSystemEvent` 与 `LoadedTools` |
| `OpenClaw.Agent/OpenClawToolExecutor.cs` | `_toolsMutationLock` 字段；`ReplaceMcpTools` 方法；`_toolDeclarations` 必须可变 | grep `ReplaceMcpTools` |
| `OpenClaw.Agent/AgentSystemPromptBuilder.cs` | `BuildDynamicSuffix` / `BuildRuntimeSection` / `NormalizeOsPlatform` / `ResolveShell` | grep `BuildDynamicSuffix` |
| `OpenClaw.Agent/Tools/DelegateTool.cs` | default lambda 抛 `InvalidOperationException`（严禁 `new AgentRuntime`） | grep `new AgentRuntime` 应为 0 |
| `OpenClaw.Agent/Plugins/McpServerToolRegistry.cs` | `WorkspaceMcpReloadResult` record + `ReloadWorkspaceServersAsync` no-op | grep `ReloadWorkspaceServersAsync` |
| `OpenClaw.Core/Models/GatewayConfig.cs` | `LlmProviderConfig.SupportsVision`；`ChannelsConfig` 三属性 `Feishu` / `DingTalk` / `WeCom` | grep `SupportsVision` 与 `WeComChannelConfig` |
| `OpenClaw.Core/Models/WebSocketEnvelopes.cs` | `WsServerEnvelope.Artifact` / `StageGate` / `ArtifactType` | grep `StageGate` |
| `OpenClaw.Core/Models/OperatorApiModels.cs` | `SessionMetadataSnapshot.HandoffItems`、`SessionMetadataUpdateRequest.HandoffItems` | grep `HandoffItems` |
| `OpenClaw.Core/Models/AdminApiModels.cs` | `DigitalEmployeeUploadResponse` / `WorkspaceUploadResponse` / `WorkspaceTreeEntry` / `WorkspaceTreeResponse` | grep 上述类名 |
| `OpenClaw.Core/Models/Session.cs` | `CoreJsonContext` 末尾的 16+ `[JsonSerializable]` 注解（包含 `SkillArtifact`、`SessionHandoffItem`、`FeishuChannelConfig` 等） | 与 `Session.cs.bak` 比对 |
| `OpenClaw.Core/Models/KingcrabHandoffModels.cs` | 7 个 handoff/stage-gate 类型（**整文件 kingcrab-only**，不会被 sync 覆盖，但被引用方一旦丢失编译会错） | 文件存在即可 |
| `OpenClaw.Core/Models/KingcrabChannelConfigs.cs` | Feishu / DingTalk / WeCom Config（**kingcrab-only 文件**） | 文件存在即可，**注意不要再去 GatewayConfig.cs 里重复定义** |
| `OpenClaw.Gateway/Composition/CoreServicesExtensions.cs` | 删除 `AddTickerQ` / `NativeAgentRuntimeFactory` / `CronSchedulerStartupService` 三类注册 | grep `AddTickerQ\|NativeAgentRuntimeFactory\|CronSchedulerStartupService` 应为注释 |
| `OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs` | 删除 `RecordLegacyMafConfigNotice` 调用 + `if (agentRuntime is AgentRuntime concreteRuntime)` 整块 native compact-callback | grep 上述两处 |
| `OpenClaw.Gateway/Pipeline/CronScheduler.cs` | 用 `NCrontab.CrontabSchedule.Parse`，禁止 `using TickerQ.Utilities` | grep `TickerQ\.Utilities` 应为 0 |

> **保险机制**：迁移过程中保留同名的 `*.cs.bak`（kingcrab 上一版的拷贝），用于和上游覆盖结果做 diff，快速发现独有成员是否丢失。

### 5.3 命名空间重映射对照表（必跑）

| 上游 | 替换为 |
| --- | --- |
| `using OpenClaw.MicrosoftAgentFrameworkAdapter;` | `using OpenClaw.Agent;` |
| `using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;` | `using OpenClaw.Agent.A2A;`（如不存在则删除该测试） |
| `services.AddOpenClawMicrosoftExtensionsAi*` | 整行删除 |
| `services.AddOpenClawSemanticKernel*` | 整行删除 |
| `using TickerQ.*;` | 整行删除 |
| `services.AddTickerQ()` / `app.UseTickerQ()` | 整行删除 |

---

## 6. Task E csproj/sln 校准

```powershell
# 验证：以下命令必须 0 命中
rg --type csproj 'TickerQ|MEAI|onnx|Microsoft\.SemanticKernel' .

# 验证：sln 中无排除项目
rg 'OpenClaw\.MicrosoftAgentFrameworkAdapter|OpenClaw\.SemanticKernelAdapter|OpenClaw\.Providers\.MicrosoftExtensionsAI|OpenClaw\.Embeddings\.Onnx' OpenClaw.Net.slnx
```

---

## 7. Task F 构建迭代节奏

```powershell
$iter = 1   # 顺序递增
dotnet build OpenClaw.Net.slnx -c Debug 2>&1 |
  Tee-Object -FilePath "docs\migration\build-logs\build-iter-$iter.log" |
  Select-String -Pattern 'error\s+CS' |
  Measure-Object | Select-Object -ExpandProperty Count
```

**迭代诊断顺序**（避免迷失方向）：
1. 先看错误数变化趋势：上一轮 → 这一轮，**升高很正常**——它是某个项目编译通过后下游错误一次性暴露。
2. 按文件聚合：`Select-String 'error CS' build-iter-N.log | Group-Object -Property {<file>}`。
3. **优先修上游层**（OpenClaw.Core → OpenClaw.Agent → OpenClaw.Gateway → AppHost），因为下游错误很多是上游缺失导致的级联。
4. **同类错误批量修**：4 个 CS0535 一般是同一个接口签名问题；几十个 CS0246 一般是同一个类型缺失。

历史曲线参考：iter-12 (绿) → iter-13 (4) → iter-14 (4) → iter-15 (32) → iter-16 (222) → iter-17 (6) → iter-18 (0) → iter-19 (0)。**220+ 错误也能 1-2 轮内归零**，不要慌。

---

## 8. Task G 交付文档清单

每轮迁移收尾时必须更新：
1. [exclusion-list.md](./exclusion-list.md)：新增的排除项追加，错误措辞修正。
2. [migration-summary.md](./migration-summary.md)：路线决策、独有成员清单、构建迭代曲线。
3. `build-logs/build-iter-*.log`：保留全部历史。
4. **本 playbook**：发现新坑时追加到 §5.2 / §6 / §7。

---

## 9. 容易踩的坑（Lessons Learned）

| 坑 | 表现 | 解决 |
| --- | --- | --- |
| 上游 sync 后 CoreJsonContext 注解丢失 | 下游运行时 `JsonSerializerContext` 找不到类型，AOT 路径直接挂 | 每次 sync 后用 `Session.cs.bak` 与新版做 diff |
| KingcrabHandoffModels.cs / KingcrabChannelConfigs.cs **被遗忘** | CS0246 大量缺失 `HandoffConfig` / `FeishuChannelConfig` | 这两个文件是 kingcrab-only 不会被 sync 删，但**容易误以为类型该在 upstream 文件里再写一遍** → 重复定义 CS0101 |
| FractalMemoryMcpProvider 误归 native | CS0234 找不到 `OpenClaw.Agent.Memory` | 判断准则：依赖 `IAgentRuntime` 接口 OK；依赖具体 `AgentRuntime` 类才是 native |
| CronSchedulerStartupService 被错误保留 | 违反"TickerQ 依赖剔除规范"；hosted tick loop 仍在跑 | 整链剔除：删文件 + 删 DI 注册 + 文档同步 |
| `IAgentRuntime` 默认成员位置 | 上游覆盖把 C# 默认接口实现也冲掉 | 默认成员（`LoadedTools`、`ApplyMcpToolChangesAsync`）写在 kingcrab 这一份接口里，sync 后必须验证 |

---

## 10. 一句话总结

> **每次 sync 后立刻按 §5.2 跑独有清单 grep 校验，比事后看构建错误便宜 10 倍。**
