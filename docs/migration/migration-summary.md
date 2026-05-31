# openclaw.net → kingcrab 迁移总结

> 交付时间：build-iter-18 通过当日。
> 关联文件：[exclusion-list.md](./exclusion-list.md)、[only-in-openclaw-copy-report.md](./only-in-openclaw-copy-report.md)、`build-logs/build-iter-1..18.log`。

## 1. 最终构建结果

| 指标 | 值 |
| --- | --- |
| 解决方案 | `OpenClaw.Net.slnx`（19 个项目） |
| 配置 | `Debug` / `net10.0` |
| 错误 | **0** |
| 警告 | **0** |
| 用时 | 约 2 分 13 秒 |
| 入口编排 | `Kingcrab.AppHost`（.NET Aspire） |
| 日志 | `docs/migration/build-logs/build-iter-18.log` |

## 2. 路线决策

- **MAF-only**：MAF（Microsoft Agent Framework）类型直接内联到 [`OpenClaw.Agent`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent)（`Maf*.cs`），不引入 SK / MEAI / Onnx 桥接项目。
- **Native runtime 排除**：[`AgentRuntime.cs`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/AgentRuntime.cs) / [`NativeAgentRuntimeFactory.cs`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs) / [`RuntimeInitializationExtensions.MafConfigNotices.cs`](file:///e:/gitee/kingcrab/src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.MafConfigNotices.cs) 不迁入。
- **TickerQ 不迁移**：放弃上游第三方调度依赖。`CronScheduler` 类本身保留（基于 NCrontab，仅作为 cron 表达式工具/状态查询），但 hosted tick loop（`CronSchedulerTickerFunction` 与 `CronSchedulerStartupService`）整体剔除。
- **Aspire 编排**：保留 [`Kingcrab.AppHost`](file:///e:/gitee/kingcrab/Kingcrab.AppHost) / [`Kingcrab.ServiceDefaults`](file:///e:/gitee/kingcrab/Kingcrab.ServiceDefaults) 不被上游覆盖。

## 3. 任务进度（plan 对应）

| Task | 状态 | 备注 |
| --- | --- | --- |
| A 基线刷新 | ✅ | 全部 diff 报告重生成 |
| B 孤儿测试清理 | ✅ | 删除 SK / MAF / MEAI 引用测试 |
| C-0 ~ C-8 增量复制 | ✅ | 内容已在 [only-in-openclaw-copy-report.md](./only-in-openclaw-copy-report.md) 列示 |
| D 共有文件三方合并 | ✅ | 详见第 4 节 |
| D-1 批量复制 + 命名空间重映射 | ✅ | 157 个文件 |
| D-2 SkillProjection 类型恢复 | ✅ | |
| D-3 NCrontab 替换 TickerQ | ✅ | `CronScheduler` 改造 |
| D-4 SkillDefinition 补齐 | ✅ | ProjectionContracts/ArtifactContract/ProjectionDiscovery |
| D-5 下游 106 errors 修复 | ✅ | 详见第 4 节 |
| E csproj/sln 校准 | ✅ | 0 错误 0 警告即证 sln 与包引用对齐 |
| F 构建验证 | ✅ | iter-1..18，最终 iter-18 通过 |
| G 交付文档 | ✅ | 本文件 + exclusion-list |

## 4. TickerQ 剔除连锁修复纪要

TickerQ 回滚（plan §1.1）后，由于上游同步覆盖了若干"共有"文件，`OpenClaw.Core` / `OpenClaw.Agent` / `OpenClaw.Gateway` 的 kingcrab 独有成员被冲掉。最终通过以下修复全部恢复：

### 4.1 `OpenClaw.Agent` 接口/工具补齐
- [`IAgentRuntime`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/IAgentRuntime.cs) 增加 `bool isSystemEvent = false` 默认参数（`RunAsync` / `RunStreamingAsync`），并新增默认接口成员 `LoadedTools` / `ApplyMcpToolChangesAsync`。
- [`OpenClawToolExecutor`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/OpenClawToolExecutor.cs) 把 `_toolDeclarations` 改为可变并新增线程安全的 `ReplaceMcpTools` 方法 + `_toolsMutationLock`。
- [`AgentSystemPromptBuilder`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/AgentSystemPromptBuilder.cs) 补回 `BuildDynamicSuffix` / `BuildRuntimeSection` / `NormalizeOsPlatform` / `ResolveShell` 4 个静态方法。
- [`DelegateTool`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/Tools/DelegateTool.cs) default lambda 改为抛 `InvalidOperationException`（kingcrab 排除 native `AgentRuntime`）。
- [`McpServerToolRegistry`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs) 新增 `WorkspaceMcpReloadResult` record + `ReloadWorkspaceServersAsync` no-op stub。
- [`FractalMemoryMcpProvider`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/Memory/FractalMemoryMcpProvider.cs) 从 upstream 复制（之前被 exclusion-list 误归类为 native）。

### 4.2 `OpenClaw.Core` 模型补齐
- [`KingcrabHandoffModels.cs`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/KingcrabHandoffModels.cs) 新建，集中沉淀 7 个 kingcrab 独有 handoff/stage-gate 类型。
- [`WebSocketEnvelopes`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/WebSocketEnvelopes.cs) `WsServerEnvelope` 新增 `Artifact` / `StageGate` / `ArtifactType`。
- [`OperatorApiModels`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/OperatorApiModels.cs) `SessionMetadataSnapshot` / `SessionMetadataUpdateRequest` 新增 `HandoffItems`。
- [`AdminApiModels`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/AdminApiModels.cs) 新增 `DigitalEmployeeUploadResponse` / `WorkspaceUploadResponse` / `WorkspaceTreeEntry` / `WorkspaceTreeResponse`。
- [`GatewayConfig`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/GatewayConfig.cs) `LlmProviderConfig` 新增 `SupportsVision`；`ChannelsConfig` 接入已存在的 [`KingcrabChannelConfigs`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/KingcrabChannelConfigs.cs)（Feishu / DingTalk / WeCom）。
- [`Session.cs`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Models/Session.cs) 中 `CoreJsonContext` 补齐 16 个 `[JsonSerializable]` 注解。

### 4.3 `OpenClaw.Gateway` 启动/调度
- `CronSchedulerStartupService` / `CronSchedulerTickerFunction` 均已删除（与 TickerQ 同属调度宿主，整体剔除）。无 hosted tick loop 注册。
- [`CronScheduler`](file:///e:/gitee/kingcrab/src/OpenClaw.Core/Pipeline/CronScheduler.cs) 类本身保留，但已切换到 `NCrontab.CrontabSchedule.Parse`，不再依赖 `TickerQ.Utilities` 命名空间；可作为 cron 表达式工具/状态查询从 DI 解析。
- [`CoreServicesExtensions`](file:///e:/gitee/kingcrab/src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs)：移除 `services.AddTickerQ()`、`AddSingleton<CronSchedulerStartupService>` + `AddHostedService<CronSchedulerStartupService>`、`AddSingleton<IAgentRuntimeFactory, NativeAgentRuntimeFactory>`；MAF factory 由 [`MafServiceCollectionExtensions`](file:///e:/gitee/kingcrab/src/OpenClaw.Agent/MafServiceCollectionExtensions.cs) 注册。
- [`RuntimeInitializationExtensions`](file:///e:/gitee/kingcrab/src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs)：剔除 `RecordLegacyMafConfigNotice` 调用 + native `AgentRuntime is concreteRuntime` compact-callback 块。
- `Program.cs` 中 `app.UseTickerQ()` 调用已移除。
- `OpenClaw.Core.csproj` 不含 `TickerQ` 包引用，仅保留 `NCrontab 3.3.0`。

## 5. 构建迭代曲线

| iter | errors | 备注 |
| ---- | ------ | ---- |
| 1–11 | 多轮 | 内容合并 + 命名空间重映射 |
| 12 | 通过 | 首次全量绿，TickerQ 仍在 |
| 13 | 4 | TickerQ 回滚后 IAgentRuntime 签名缺失 |
| 14 | 4 | 4 类 kingcrab 独有成员缺失 |
| 15 | 32 | OpenClaw.Agent 编译后 Gateway 暴露问题 |
| 16 | 222 | Gateway 全编译后下游一次性暴露 |
| 17 | 6 | KingcrabChannelConfigs.cs 重复定义 |
| **18** | **0** | ✅ 收官 |

## 6. 后续维护建议

1. **再次同步上游时**：先重跑 `docs/migration/diff-*.md` 生成对照，再合并；本次新增的 kingcrab 独有成员（第 4 节列出）务必保留。
2. **TickerQ 永久排除**：如上游再次提交 TickerQ 相关代码，按 [exclusion-list.md §1.1](./exclusion-list.md) 处理。`CronSchedulerStartupService` / `CronSchedulerTickerFunction` 已永久剔除，不要回退；如需调度能力，请在新设计的 BackgroundService 里显式调用 `CronScheduler.RunTickAsync`。
3. **AOT 评估**：`CoreJsonContext` 注解已对齐到本次新增类型，若再加 DTO 请同步追加 `[JsonSerializable]` 否则 NativeAOT 路径会失败。
4. **MCP 工作区热重载**：`McpServerToolRegistry.ReloadWorkspaceServersAsync` 当前为 no-op stub，待后续按需填实——不影响启动期通过 `RegisterToolsAsync` 完成的常规注册。
