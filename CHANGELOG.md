# Changelog

All notable changes to this project are tracked in this file.

## [Unreleased] - 2026-03-05

### openclaw.net 迁入 kingcrab（结构性合并）
- 新增 9 个 A 组项目（整项拷贝）：`OpenClaw.SkillKit`、`OpenClaw.SkillKit.Abstractions`、`OpenClaw.Payments.Abstractions`、`OpenClaw.Payments.Core`、`OpenClaw.Payments.StripeLink`、`OpenClaw.Plugins.Payment`、`OpenClaw.Dashboard`（Blazor WASM）、`OpenClaw.Testing`，并注册到 `OpenClaw.Net.slnx`。
- 排除（B 组）：`OpenClaw.MicrosoftAgentFrameworkAdapter`（已合并进 kingcrab `OpenClaw.Agent`）、`OpenClaw.Providers.MicrosoftExtensionsAI`（MEAI Provider）、`OpenClaw.SemanticKernelAdapter`（SK 适配器）。
- 排除（C 组）：`OpenClaw.Embeddings.Onnx`（源代码缺失）、`whatsapp-whatsmeow-worker/`（保留 kingcrab 现有 Baileys Worker）、`samples/*`（不引入 samples 目录）。
- 公共项目（D 组）按文件级 diff 合并：`only-in-openclaw` 文件批量拷贝（328 copied / 7 skipped），`common` 文件保留 kingcrab 已修复版本不动；详细差异报告见 `docs/migration/diff-*.md`。
- 因 kingcrab 与 openclaw.net 在 `OperatorDashboardModels`、`OperatorGovernanceModels`、`FractalMemoryConfig`、`UrlSafetyConfig`、`SkillResource`、`MemoryNoteCatalog`、`HarnessRegressionScenarios` 等内部抽象上分叉，迁移期已剔除依赖缺失类型的 only-in-openclaw 源文件（清单见 `docs/migration/build-logs/deleted.txt`），优先保证 kingcrab 现有补丁不被回退。
- 移除 `OpenClaw.Plugins.Mempalace` 整项（因 `IMemoryNoteCatalog`、`MemoryMempalaceConfig`、`NativeDynamicMemoryProviderContext` 等抽象在 kingcrab 不存在，且核心两类文件不可独立编译；如未来需要 MemPalace，可基于 kingcrab `IMemoryStore` 重新实现）。
- 移除 `OpenClaw.Agent\Tools\ToolPathPolicy.cs`（与 kingcrab 既有 `OpenClaw.Core.Security.ToolPathPolicy` 命名冲突）。
- `OpenClaw.Payments.Core/PaymentServiceCollectionExtensions.cs` 与 `OpenClaw.Payments.StripeLink/LinkCliCommandRunner.cs` 中的 `ISensitiveDataRedactor` / `PaymentSensitiveDataRedactor` 绑定改为本地 stub（直通输出），原因同上。
- `OpenClaw.Testing/HarnessRegressionRunner.cs` 默认场景列表改为空集（原依赖的 `HarnessRegressionScenarios` 因引用 kingcrab 不存在的多种 API 已删除）。
- `Directory.Build.props` 增补 Gateway 的 `OpenClawFeatureVariant` 输出目录隔离规则（来自 openclaw.net）。
- `OpenClaw.Gateway/Endpoints/OpenAiEndpoints.cs` 顶层类型加 `partial` 修饰符，与新引入的 `OpenAiEndpoints.ChatCompletions.cs` / `OpenAiEndpoints.Responses.cs` 配套。
- 包版本对齐：`Payments.Core` 与 `Payments.StripeLink` 的 `Microsoft.Extensions.DependencyInjection.Abstractions` / `Microsoft.Extensions.Logging.Abstractions` 升级到 `10.0.7`，避免 NU1605 降级冲突。
- Aspire AppHost 暂未注册 `OpenClaw.Dashboard`（Blazor WASM 独立工程，缺独立服务宿主），后续如需托管再单独评估。

## [Previously Unreleased] - 2026-03-04

### Integration API, MCP, and SDK
- Added a gateway-hosted typed integration API under `/api/integration` for operational reads and inbound message enqueueing.
- Added typed integration read models for dashboard snapshots, approvals, approval history, providers, plugins, operator audit, session detail, and session timelines.
- Added a gateway-hosted MCP JSON-RPC facade at `/mcp` over the shared integration/runtime surface.
- Added starter MCP contracts for initialize, tool listing/calling, resource listing/reading, resource templates, and prompt listing/retrieval.
- Added a reusable `IntegrationApiFacade` so the integration API, MCP facade, and operator dashboard share the same gateway-side read logic.
- Added a shared `OpenClaw.Client` package and expanded `OpenClawHttpClient` with typed integration API methods plus MCP helpers such as `InitializeMcpAsync`, `ListMcpToolsAsync`, `ReadMcpResourceAsync`, `GetMcpPromptAsync`, and `CallMcpToolAsync`.
- Repointed the operator dashboard read paths to the typed integration API while keeping the existing admin mutation flows intact.

### Security
- Bound tool-approval decisions to the original requester (`channelId` + `senderId`) for non-loopback/public binds.
- Kept `POST /tools/approve` as an explicit admin override path.
- Added WhatsApp official webhook signature validation support (`ValidateSignature`, `WebhookAppSecret`/`WebhookAppSecretRef`).
- Added WhatsApp bridge inbound auth validation via `Authorization: Bearer <BridgeToken>` or `X-Bridge-Token`.
- Enforced additional non-loopback startup hardening:
  - WhatsApp official mode requires signature validation + app secret.
  - WhatsApp bridge mode requires a bridge token.
- Hardened generic webhook HMAC verification:
  - `ValidateHmac=true` now requires a secret at config validation time.
  - Signature checks now use constant-time byte comparison and support `sha256=<hex>` header format.
- Hardened SQL write detection in `database` tool by tokenizing SQL and detecting write/admin keywords beyond naive prefix checks.
- Hardened `inbox_zero` IMAP command construction:
  - Quoted IMAP credentials and folders.
  - Sanitized user-provided folder names for analyze/cleanup/trash-sender actions.

### Memory Retention and Hardening
- Added opt-in memory retention configuration at `OpenClaw:Memory:Retention`:
  - `Enabled` (default `false`)
  - `RunOnStartup` (default `true`)
  - `SweepIntervalMinutes` (default `30`)
  - `SessionTtlDays` (default `30`)
  - `BranchTtlDays` (default `14`)
  - `ArchiveEnabled` (default `true`)
  - `ArchivePath` (default `./memory/archive`)
  - `ArchiveRetentionDays` (default `30`)
  - `MaxItemsPerSweep` (default `1000`)
- Added retention store abstraction (`IMemoryRetentionStore`) and new retention models:
  - `RetentionSweepRequest`
  - `RetentionSweepResult`
  - `RetentionStoreStats`
  - `RetentionRunStatus`
- Implemented retention sweep support in both file and sqlite memory stores.
- Added archive-before-delete behavior for expired sessions/branches with raw JSON archive envelopes.
- Added archive TTL purge behavior for old archive files.
- Added sqlite indexes to improve retention candidate queries:
  - `idx_sessions_updated_at`
  - `idx_branches_updated_at`
- Added proactive in-memory active-session expiry sweep (`SessionManager.SweepExpiredActiveSessions`) and wired it into periodic cleanup.
- Added background retention sweeper service (`PeriodicTimer`, overlap-safe with semaphore).
- Added retention admin endpoints:
  - `GET /memory/retention/status`
  - `POST /memory/retention/sweep` (supports `dryRun=true`)
- Extended doctor outputs (`/doctor`, `/doctor/text`) with retention config/status/stats and disabled-retention warnings for large persisted counts.
- Extended runtime metrics with retention counters and last-run status gauges.
- Corrected compaction validation semantics: when compaction is enabled, `CompactionThreshold` must be greater than `MaxHistoryTurns`.

### Usability/Safety Balance
- WebChat token persistence now defaults to session-only storage (`sessionStorage`).
- Added a `Remember` toggle to opt into persistent token storage (`localStorage`).

### Tests
- Added `ToolApprovalServiceTests` for requester-bound approvals and admin override behavior.
- Added `GatewaySecurityHardeningTests` for public-bind hardening checks (WhatsApp and raw refs).
- Expanded `GatewaySecurityTests` for HMAC signature validation.
- Expanded `ConfigValidatorTests` for webhook-HMAC-secret and WhatsApp-app-secret validation.
- Added retention validation coverage in `ConfigValidatorTests`.
- Added `FileMemoryStoreRetentionTests` (archive/delete, protected sessions, archive failure handling, archive purge).
- Added `SqliteMemoryStoreRetentionTests` (archive/delete, protected sessions, max item cap, index creation, archive failure handling).
- Added `MemoryRetentionSweeperServiceTests` (manual sweep status/metrics and overlap prevention).
- Added proactive expiry coverage in `SessionManagerTests` (`SweepExpiredActiveSessions`).
- Expanded `NativePluginTests` with SQL write-bypass regression cases.
- Expanded `SecurityTests` with InboxZero folder-sanitization coverage.
- Added focused gateway/admin endpoint coverage for the typed integration API, MCP facade, route exposure, and the shared `OpenClaw.Client` MCP surface.

### Documentation
- Updated:
  - `README.md`
  - `QUICKSTART.md`
  - `USER_GUIDE.md`
  - `SECURITY.md`
  - `CHANGELOG.md`
  - `TOOLS_GUIDE.md`

### Docker
- Fixed Docker runtime env var binding to use `OpenClaw__...` (ASP.NET configuration) for gateway bind/port/memory settings.
- Docker defaults now disable the JS plugin bridge on non-loopback binds (`OpenClaw__Plugins__Enabled=false`) unless explicitly enabled.
- Standardized default image name to `openclaw.net` for local builds and compose.
- Re-pushed Docker Hub images without provenance/SBOM to improve Docker Hub UI compatibility.
- Added `DOCKERHUB.md` as paste-ready repository overview content for Docker Hub.
