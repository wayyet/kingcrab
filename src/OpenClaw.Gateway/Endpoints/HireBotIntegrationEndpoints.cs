using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static class HireBotIntegrationEndpoints
{
    private const string HireBotChannelId = "hirebot";
    private static readonly ConcurrentDictionary<string, HireBotState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly StageSkillMapping[] StageSkills =
    [
        new(HireStages.Goal, "skill.hirebot.goal.collect", ["business_goal", "owner", "success_metric"], "收集雇佣目标、负责人和成功标准"),
        new(HireStages.Scenario, "skill.hirebot.scenario.collect", ["user_profile", "trigger_event", "expected_outcome"], "收集业务场景、触发条件和期望结果"),
        new(HireStages.Systems, "skill.hirebot.systems.collect", ["system_list", "permission_scope", "data_sources"], "收集系统清单、权限范围和数据来源"),
        new(HireStages.Gaps, "skill.hirebot.gaps.collect", ["blockers", "risk_level", "fallback_plan"], "收集风险缺口与回退方案"),
        new(HireStages.Package, "skill.hirebot.package.prepare", ["runbook", "acceptance_criteria", "delivery_window"], "收集交付运行手册、验收标准和交付窗口"),
    ];

    public static void MapOpenClawHireBotIntegrationEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var facade = IntegrationApiFacade.Create(startup, runtime, app.Services);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HireBotIntegration");
        var group = app.MapGroup("/api/integration/hirebot").WithTags("HireBot Integration");

        group.MapPost("/hirings", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_create", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            var request = await ReadJsonAsync<HireTemplateRequest>(ctx, ctx.RequestAborted);
            if (request is null ||
                string.IsNullOrWhiteSpace(request.TemplateId) ||
                string.IsNullOrWhiteSpace(request.TenantId) ||
                string.IsNullOrWhiteSpace(request.OperatorId))
            {
                return Results.BadRequest(new { message = "templateId、tenantId、operatorId 为必填项" });
            }

            var hireId = $"hire_{Guid.NewGuid():N}";
            var ownerSub = ResolveOwnerSubject(ctx, request.TenantId, request.OperatorId);
            var senderId = BuildSenderId(ownerSub, request.OperatorId);
            var sessionId = $"{HireBotChannelId}:{hireId}";

            try
            {
                var (sandboxId, volumePath) = await CreateSandboxAsync(startup.Config, hireId, ownerSub, ctx.RequestAborted);

                var state = new HireBotState
                {
                    HireId = hireId,
                    TemplateId = request.TemplateId.Trim(),
                    TenantId = request.TenantId.Trim(),
                    OperatorId = request.OperatorId.Trim(),
                    OwnerSubject = ownerSub,
                    SandboxId = sandboxId,
                    SessionId = sessionId,
                    ChannelId = HireBotChannelId,
                    SenderId = senderId,
                    VolumePath = volumePath,
                    Status = HireStatuses.Ready,
                    CollectionPhase = HireCollectionPhases.NotStarted,
                    CurrentStage = HireStages.Goal,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };

                // 初始化一条系统消息，前端可直接展示会话已就绪状态。
                state.Messages.Add(new ConversationMessage(
                    MessageId: $"msg_{Guid.NewGuid():N}",
                    Role: "assistant",
                    Content: "会话已启动，请描述雇佣目标与业务场景。",
                    CreatedAt: DateTimeOffset.UtcNow));

                States[hireId] = state;
                await PersistConversationToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);

                return Results.Json(new HireTemplateResult(
                    HireId: hireId,
                    SandboxId: sandboxId,
                    Status: state.Status,
                    NextAction: $"/api/integration/hirebot/hirings/{hireId}"));
            }
            catch (OperationCanceledException ex) when (ctx.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "HireBot 创建实例请求被取消: HireId={HireId}, Owner={Owner}",
                    hireId,
                    ownerSub);
                return Results.Json(
                    new
                    {
                        code = 499,
                        message = "创建实例请求已取消"
                    },
                    statusCode: 499);
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(
                    ex,
                    "HireBot 创建实例超时: HireId={HireId}, Owner={Owner}",
                    hireId,
                    ownerSub);
                return Results.Json(
                    new
                    {
                        code = 504,
                        message = "创建 OpenSandbox 实例超时"
                    },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HireBot 创建实例失败: HireId={HireId}, Owner={Owner}", hireId, ownerSub);
                return Results.Json(
                    new
                    {
                        code = 502,
                        message = "创建 OpenSandbox 实例失败"
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/hirings/{hireId}", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_status", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            return Results.Json(new HireStatusResult(
                HireId: state.HireId,
                SandboxId: state.SandboxId,
                Status: state.Status,
                ErrorCode: state.ErrorCode,
                ErrorMessage: state.ErrorMessage,
                CollectionPhase: state.CollectionPhase,
                CurrentStage: state.CurrentStage));
        });

        group.MapPost("/hirings/{hireId}/conversation/start", async (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_start", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            IResult? lockedFailure = null;
            StartConversationResult? response = null;
            lock (state.SyncRoot)
            {
                if (state.Status != HireStatuses.Ready)
                {
                    lockedFailure = Results.Json(
                        new { message = "实例未就绪，请稍后重试" },
                        statusCode: StatusCodes.Status409Conflict);
                }
                else
                {
                    state.CollectionPhase = state.Messages.Any(static message =>
                        string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                        ? HireCollectionPhases.InProgress
                        : HireCollectionPhases.NotStarted;
                    state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    response = new StartConversationResult(
                        HireId: state.HireId,
                        SessionId: state.SessionId,
                        CurrentStage: state.CurrentStage,
                        RequiresAudit: false,
                        StageSkills: StageSkills);
                }
            }

            if (lockedFailure is not null)
            {
                return lockedFailure;
            }

            await PersistConversationToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);
            return Results.Json(response!);
        });

        group.MapPost("/hirings/{hireId}/conversation/messages", async (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_send", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            var request = await ReadJsonAsync<ConversationMessageRequest>(ctx, ctx.RequestAborted);
            if (request is null || (string.IsNullOrWhiteSpace(request.Content) && (request.StructuredAnswers is null || request.StructuredAnswers.Count == 0)))
            {
                return Results.BadRequest(new { message = "content 与 structuredAnswers 不能同时为空" });
            }

            if (state.Status != HireStatuses.Ready)
            {
                return Results.Json(new { message = "实例未就绪，请稍后重试" }, statusCode: StatusCodes.Status409Conflict);
            }

            var userText = string.IsNullOrWhiteSpace(request.Content) ? "补充信息" : request.Content.Trim();
            string outboundText;
            lock (state.SyncRoot)
            {
                outboundText = BuildRuntimePrompt(state, userText, request.StructuredAnswers);
            }
            var baselineAssistantCount = await CountAssistantTurnsAsync(runtime, state.SessionId, ctx.RequestAborted);

            await facade.QueueMessageAsync(
                new IntegrationMessageRequest
                {
                    ChannelId = state.ChannelId,
                    SenderId = state.SenderId,
                    SessionId = state.SessionId,
                    Text = outboundText,
                    MessageId = $"msg_{Guid.NewGuid():N}"
                },
                ctx.RequestAborted);

            var assistantTurn = await WaitForAssistantTurnAsync(
                runtime,
                state.SessionId,
                baselineAssistantCount,
                TimeSpan.FromSeconds(20),
                ctx.RequestAborted);

            var assistantContent = string.IsNullOrWhiteSpace(assistantTurn?.Content)
                ? "已收到你的信息，正在继续整理。"
                : assistantTurn.Content.Trim();

            ConversationMessage assistantMessage;
            StagePreview preview;

            lock (state.SyncRoot)
            {
                var userMessage = new ConversationMessage(
                    MessageId: $"msg_{Guid.NewGuid():N}",
                    Role: "user",
                    Content: userText,
                    CreatedAt: DateTimeOffset.UtcNow);
                state.Messages.Add(userMessage);

                assistantMessage = new ConversationMessage(
                    MessageId: $"msg_{Guid.NewGuid():N}",
                    Role: "assistant",
                    Content: assistantContent,
                    CreatedAt: assistantTurn?.Timestamp ?? DateTimeOffset.UtcNow);
                state.Messages.Add(assistantMessage);

                if (request.StructuredAnswers is not null)
                {
                    foreach (var pair in request.StructuredAnswers)
                    {
                        state.CollectedFields[pair.Key] = pair.Value;
                    }
                }

                state.CurrentStage = ResolveCurrentStage(state.CollectedFields);
                state.CollectionPhase = IsCollectionReadyForFinalize(state.CollectedFields)
                    ? HireCollectionPhases.ReadyForFinalize
                    : HireCollectionPhases.InProgress;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;

                preview = BuildStagePreview(state, request.StructuredAnswers, assistantContent);
                state.LatestPreview = preview;
            }

            await PersistConversationToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);

            return Results.Json(new ConversationResult(
                HireId: state.HireId,
                SessionId: state.SessionId,
                CurrentStage: state.CurrentStage,
                RequiresAudit: false,
                AssistantMessage: assistantMessage,
                LatestPreview: preview));
        });

        group.MapGet("/hirings/{hireId}/conversation/messages", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_timeline", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            IReadOnlyList<ConversationMessage> timeline;
            lock (state.SyncRoot)
            {
                timeline = state.Messages.ToArray();
            }

            return Results.Json(new ConversationTimelineResult(
                HireId: state.HireId,
                SessionId: state.SessionId,
                CurrentStage: state.CurrentStage,
                RequiresAudit: false,
                CollectionPhase: state.CollectionPhase,
                Messages: timeline,
                StageSkills: StageSkills));
        });

        group.MapGet("/hirings/{hireId}/stage-preview", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_preview", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            lock (state.SyncRoot)
            {
                var preview = state.LatestPreview ?? BuildStagePreview(state, null, "尚未产生预览，请先发送消息。");
                return Results.Json(preview);
            }
        });

        group.MapPost("/hirings/{hireId}/audit-decisions", async (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_audit", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            var request = await ReadJsonAsync<AuditDecisionRequest>(ctx, ctx.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Stage) || string.IsNullOrWhiteSpace(request.Decision))
            {
                return Results.BadRequest(new { message = "stage 与 decision 为必填项" });
            }

            lock (state.SyncRoot)
            {
                var stageName = request.Stage.Trim().ToUpperInvariant();
                var stageSkill = ResolveStageSkillMapping(stageName);
                state.AuditLogs.Add(new AuditLog(
                    LogId: $"audit_{Guid.NewGuid():N}",
                    Stage: stageName,
                    SkillName: stageSkill.SkillName,
                    Decision: request.Decision.Trim().ToUpperInvariant(),
                    Actor: ResolveOwnerSubject(ctx, state.TenantId, state.OperatorId),
                    Comment: request.Comment,
                    InputDigest: ComputeDigest(request.Stage + request.Decision),
                    OutputDigest: ComputeDigest(state.CurrentStage + state.CollectionPhase),
                    TimestampUtc: DateTimeOffset.UtcNow));
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            await PersistConversationToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);

            return Results.Json(new AuditDecisionResult(
                HireId: state.HireId,
                Stage: request.Stage.Trim().ToUpperInvariant(),
                Decision: request.Decision.Trim().ToUpperInvariant(),
                CurrentStage: state.CurrentStage,
                RequiresAudit: false,
                CollectionPhase: state.CollectionPhase));
        });

        group.MapGet("/hirings/{hireId}/audit-logs", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_audit_logs", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            lock (state.SyncRoot)
            {
                return Results.Json(state.AuditLogs
                    .OrderByDescending(static x => x.TimestampUtc)
                    .ToArray());
            }
        });

        group.MapPost("/hirings/{hireId}/finalize", async (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_finalize", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            var session = await runtime.SessionManager.LoadAsync(state.SessionId, ctx.RequestAborted);
            var artifacts = BuildArtifactFiles(state, session);
            var archive = BuildZipArchive(artifacts);

            lock (state.SyncRoot)
            {
                state.ArtifactFiles.Clear();
                foreach (var pair in artifacts)
                {
                    state.ArtifactFiles[pair.Key] = pair.Value;
                }

                state.ArtifactArchive = archive;
                state.ArtifactArchiveFileName = $"{state.HireId}_artifacts.zip";
                state.CurrentStage = HireStages.Done;
                state.CollectionPhase = HireCollectionPhases.Finalized;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            await PersistConversationToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);
            await PersistArtifactsToSandboxAsync(state, startup.Config, logger, ctx.RequestAborted);

            return Results.Json(new FinalizeResult(
                HireId: state.HireId,
                CurrentStage: state.CurrentStage,
                CollectionPhase: state.CollectionPhase,
                GeneratedFiles: artifacts.Keys.ToArray(),
                DownloadUrl: $"/api/integration/hirebot/hirings/{state.HireId}/artifacts/download"));
        });

        group.MapGet("/hirings/{hireId}/workflow", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_workflow", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            lock (state.SyncRoot)
            {
                return Results.Json(new WorkflowStateResult(
                    HireId: state.HireId,
                    SessionId: state.SessionId,
                    CurrentStage: state.CurrentStage,
                    RequiresAudit: false,
                    CollectionPhase: state.CollectionPhase,
                    StageSkills: StageSkills,
                    AuditLogs: state.AuditLogs
                        .OrderByDescending(static x => x.TimestampUtc)
                        .ToArray()));
            }
        });

        group.MapGet("/hirings/{hireId}/artifacts/download", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_download", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            lock (state.SyncRoot)
            {
                if (state.ArtifactArchive is null || state.ArtifactArchive.Length == 0 || string.IsNullOrWhiteSpace(state.ArtifactArchiveFileName))
                {
                    return Results.Json(new { message = "交付包尚未生成，请先执行 finalize。" }, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.File(state.ArtifactArchive, "application/zip", state.ArtifactArchiveFileName);
            }
        });

        // 与数据卷下载语义保持一致，提供一个别名端点。
        group.MapGet("/hirings/{hireId}/volume/download", (HttpContext ctx, string hireId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "hirebot_http_volume_download", requireCsrf: false);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryGetStateForCaller(ctx, startup.Config, logger, hireId, out var state, out var ownerFailure))
            {
                return ownerFailure!;
            }

            lock (state.SyncRoot)
            {
                if (state.ArtifactArchive is null || state.ArtifactArchive.Length == 0 || string.IsNullOrWhiteSpace(state.ArtifactArchiveFileName))
                {
                    return Results.Json(new { message = "数据卷导出包尚未生成，请先执行 finalize。" }, statusCode: StatusCodes.Status409Conflict);
                }

                return Results.File(state.ArtifactArchive, "application/zip", state.ArtifactArchiveFileName);
            }
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonOptions, cancellationToken);
        }
        catch
        {
            return default;
        }
    }

    private static IResult? AuthorizeAndConsume(
        HttpContext ctx,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions,
        string endpointScope,
        bool requireCsrf)
    {
        var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf);
        if (!auth.IsAuthorized)
        {
            return Results.Unauthorized();
        }

        if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, runtime.Operations, auth, endpointScope, out var blockedByPolicyId))
        {
            return Results.Json(
                new
                {
                    success = false,
                    error = $"Rate limit exceeded by policy '{blockedByPolicyId}'."
                },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return null;
    }

    private static bool TryGetStateForCaller(HttpContext ctx, GatewayConfig config, ILogger logger, string hireId, out HireBotState state, out IResult? failure)
    {
        failure = null;
        state = default!;
        if (string.IsNullOrWhiteSpace(hireId))
        {
            failure = Results.NotFound(new { message = "雇佣流程不存在" });
            return false;
        }

        var normalizedHireId = hireId.Trim();
        if (!States.TryGetValue(normalizedHireId, out state!))
        {
            if (!TryRestoreStateFromSnapshot(config, logger, normalizedHireId, out state))
            {
                failure = Results.NotFound(new { message = "雇佣流程不存在" });
                return false;
            }

            States[normalizedHireId] = state;
            logger.LogInformation("从持久化快照恢复 HireBot 状态: HireId={HireId}, SandboxId={SandboxId}", state.HireId, state.SandboxId);
        }

        var caller = ResolveOwnerSubject(ctx, state.TenantId, state.OperatorId);
        if (!string.Equals(caller, "anonymous", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(caller, state.OwnerSubject, StringComparison.OrdinalIgnoreCase))
        {
            failure = Results.Json(new { message = "无权访问该雇佣流程" }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        return true;
    }

    private static bool TryRestoreStateFromSnapshot(GatewayConfig config, ILogger logger, string hireId, out HireBotState state)
    {
        state = default!;
        var snapshotPath = BuildSnapshotFilePath(config, hireId);
        if (!File.Exists(snapshotPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(snapshotPath);
            var snapshot = JsonSerializer.Deserialize<HireBotStateSnapshot>(json, JsonOptions);
            if (snapshot is null)
            {
                return false;
            }

            state = snapshot.ToState();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取 HireBot 状态快照失败: Path={SnapshotPath}, HireId={HireId}", snapshotPath, hireId);
            return false;
        }
    }

    private static string ResolveOwnerSubject(HttpContext ctx, string tenantId, string operatorId)
    {
        var sub =
            ctx.User.FindFirst("sub")?.Value ??
            ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            return sub.Trim();
        }

        if (ctx.Request.Headers.TryGetValue("X-HireBot-Owner", out var headerValues))
        {
            var headerValue = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                return headerValue.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(operatorId))
        {
            return $"{tenantId.Trim()}:{operatorId.Trim()}";
        }

        return "anonymous";
    }

    private static string BuildSenderId(string ownerSub, string operatorId)
    {
        if (!string.IsNullOrWhiteSpace(ownerSub))
        {
            return ownerSub;
        }

        if (!string.IsNullOrWhiteSpace(operatorId))
        {
            return operatorId.Trim();
        }

        return "hirebot-operator";
    }

    private static async Task<(string SandboxId, string VolumePath)> CreateSandboxAsync(
        GatewayConfig config,
        string hireId,
        string ownerSub,
        CancellationToken cancellationToken)
    {
        var endpoint = config.Sandbox.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("OpenClaw:Sandbox:Endpoint 未配置");
        }

        var image = ResolveSandboxImage(config);
        var ttl = ResolveSandboxTtl(config);
        var readyTimeoutSeconds = ResolveSandboxReadyTimeoutSeconds(config);
        var connectionConfig = BuildConnectionConfig(config);
        var volumeRoot = ResolveVolumeRoot();
        var volumePath = $"{volumeRoot.TrimEnd('/')}/{SanitizePathSegment(ownerSub)}/{SanitizePathSegment(hireId)}";
        var resourceLimits = ResolveSandboxResourceLimits(config);
        var entrypoint = ResolveSandboxEntrypoint(config);
        var runtimeEnv = ResolveSandboxRuntimeEnv(config);
        var networkPolicy = ResolveSandboxNetworkPolicy(config);
        var skipHealthCheck = ResolveSandboxSkipHealthCheck(config);

        var createOptions = new SandboxCreateOptions
        {
            ConnectionConfig = connectionConfig,
            Image = image,
            TimeoutSeconds = ttl,
            ReadyTimeoutSeconds = readyTimeoutSeconds,
            ManualCleanup = true,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["created-by"] = "hirebot",
                ["hire-id"] = hireId,
                ["owner"] = SanitizePathSegment(ownerSub)
            }
        };

        if (resourceLimits.Count > 0)
        {
            createOptions.Resource = resourceLimits;
        }

        if (entrypoint.Length > 0)
        {
            createOptions.Entrypoint = entrypoint;
        }

        if (runtimeEnv.Count > 0)
        {
            createOptions.Env = runtimeEnv;
        }

        if (networkPolicy is not null)
        {
            createOptions.NetworkPolicy = networkPolicy;
        }

        if (skipHealthCheck.HasValue)
        {
            createOptions.SkipHealthCheck = skipHealthCheck.Value;
        }

        await using var sandbox = await Sandbox.CreateAsync(createOptions, cancellationToken);

        await sandbox.Files.CreateDirectoriesAsync(
            [
                new CreateDirectoryEntry
                {
                    Path = volumePath,
                    Mode = 755
                }
            ],
            cancellationToken);

        return (sandbox.Id, volumePath);
    }

    private static ConnectionConfig BuildConnectionConfig(GatewayConfig config)
    {
        var endpoint = config.Sandbox.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("OpenClaw:Sandbox:Endpoint 未配置");
        }

        var uri = new Uri(endpoint.Trim(), UriKind.Absolute);
        var protocol = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? ConnectionProtocol.Https
            : ConnectionProtocol.Http;
        var domain = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var apiKey = ResolveSandboxApiKey(config.Sandbox.ApiKey);

        return new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = domain,
            Protocol = protocol,
            ApiKey = apiKey
        });
    }

    private static string? ResolveSandboxApiKey(string? configuredApiKey)
    {
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return null;
        }

        return configuredApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
               configuredApiKey.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)
            ? SecretResolver.Resolve(configuredApiKey)
            : configuredApiKey;
    }

    private static string ResolveSandboxImage(GatewayConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_IMAGE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.Sandbox.Image))
        {
            return config.Sandbox.Image.Trim();
        }

        if (config.Sandbox.Tools.TryGetValue("shell", out var shell) && !string.IsNullOrWhiteSpace(shell.Template))
        {
            return shell.Template.Trim();
        }

        if (config.Sandbox.Tools.TryGetValue("code_exec", out var codeExec) && !string.IsNullOrWhiteSpace(codeExec.Template))
        {
            return codeExec.Template.Trim();
        }

        return "alpine:3.23";
    }

    private static int ResolveSandboxTtl(GatewayConfig config)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_TTL_SECONDS"), out var fromEnv) && fromEnv > 0)
        {
            return fromEnv;
        }

        return config.Sandbox.DefaultTTL > 0 ? config.Sandbox.DefaultTTL : 3600;
    }

    private static int ResolveSandboxReadyTimeoutSeconds(GatewayConfig config)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_READY_TIMEOUT_SECONDS"), out var fromEnv) && fromEnv > 0)
        {
            return fromEnv;
        }

        return config.Sandbox.ReadyTimeoutSeconds > 0 ? config.Sandbox.ReadyTimeoutSeconds : 180;
    }

    private static Dictionary<string, string> ResolveSandboxResourceLimits(GatewayConfig config)
    {
        var resource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Sandbox.Resource)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                resource[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        var cpu = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_RESOURCE_CPU");
        if (!string.IsNullOrWhiteSpace(cpu))
        {
            resource["cpu"] = cpu.Trim();
        }

        var memory = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_RESOURCE_MEMORY");
        if (!string.IsNullOrWhiteSpace(memory))
        {
            resource["memory"] = memory.Trim();
        }

        return resource;
    }

    private static string[] ResolveSandboxEntrypoint(GatewayConfig config)
    {
        var rawEntrypoint = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_ENTRYPOINT");
        if (!string.IsNullOrWhiteSpace(rawEntrypoint))
        {
            return rawEntrypoint
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        return config.Sandbox.Entrypoint
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .ToArray();
    }

    private static Dictionary<string, string> ResolveSandboxRuntimeEnv(GatewayConfig config)
    {
        var runtimeEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in config.Sandbox.RuntimeEnv)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                runtimeEnv[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        var rawEnvJson = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_ENV_JSON");
        if (string.IsNullOrWhiteSpace(rawEnvJson))
        {
            return runtimeEnv;
        }

        try
        {
            var envFromJson = JsonSerializer.Deserialize<Dictionary<string, string>>(rawEnvJson);
            if (envFromJson is null)
            {
                return runtimeEnv;
            }

            foreach (var pair in envFromJson)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    runtimeEnv[pair.Key.Trim()] = pair.Value.Trim();
                }
            }
        }
        catch
        {
            // 忽略非法 JSON，避免配置错误影响实例创建主流程。
        }

        return runtimeEnv;
    }

    private static NetworkPolicy? ResolveSandboxNetworkPolicy(GatewayConfig config)
    {
        var configuredHosts = config.Sandbox.NetworkEgressAllowHosts
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .ToList();

        var rawHosts = Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_NETWORK_EGRESS_ALLOW_HOSTS");
        if (!string.IsNullOrWhiteSpace(rawHosts))
        {
            configuredHosts = rawHosts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        if (configuredHosts.Count == 0)
        {
            return null;
        }

        return new NetworkPolicy
        {
            DefaultAction = NetworkRuleAction.Allow,
            Egress = [.. configuredHosts.Select(static host => new NetworkRule
            {
                Action = NetworkRuleAction.Allow,
                Target = host
            })]
        };
    }

    private static bool? ResolveSandboxSkipHealthCheck(GatewayConfig config)
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable("HIREBOT_SANDBOX_SKIP_HEALTH_CHECK"), out var fromEnv))
        {
            return fromEnv;
        }

        return config.Sandbox.SkipHealthCheck;
    }

    private static string ResolveVolumeRoot()
    {
        var envValue = Environment.GetEnvironmentVariable("HIREBOT_DATA_VOLUME_ROOT");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        return "/workspace/hirebot-data";
    }

    private static string ResolveSnapshotRoot(GatewayConfig config)
    {
        var envValue = Environment.GetEnvironmentVariable("HIREBOT_STATE_SNAPSHOT_ROOT");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return Path.GetFullPath(envValue.Trim());
        }

        var storageRoot = string.IsNullOrWhiteSpace(config.Memory.StoragePath)
            ? "./memory"
            : config.Memory.StoragePath;
        var absoluteStorageRoot = Path.IsPathRooted(storageRoot)
            ? storageRoot
            : Path.GetFullPath(storageRoot, AppContext.BaseDirectory);
        return Path.Combine(absoluteStorageRoot, "hirebot-snapshots");
    }

    private static string BuildSnapshotFilePath(GatewayConfig config, string hireId)
    {
        var root = ResolveSnapshotRoot(config);
        return Path.Combine(root, $"{SanitizePathSegment(hireId)}.json");
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var input = value.Trim();
        Span<char> buffer = stackalloc char[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];
            buffer[i] = char.IsLetterOrDigit(current) || current is '-' or '_' or '.'
                ? current
                : '_';
        }

        return new string(buffer);
    }

    private static string BuildRuntimePrompt(
        HireBotState state,
        string content,
        IReadOnlyDictionary<string, string>? structuredAnswers)
    {
        var stageSkill = ResolveStageSkillMapping(state.CurrentStage);
        var mergedFields = new Dictionary<string, string?>(state.CollectedFields, StringComparer.OrdinalIgnoreCase);
        if (structuredAnswers is not null)
        {
            foreach (var pair in structuredAnswers)
            {
                mergedFields[pair.Key] = pair.Value;
            }
        }

        var missingFields = ResolveMissingFields(stageSkill, mergedFields);
        var builder = new StringBuilder();
        builder.AppendLine($"[CURRENT_STAGE] {state.CurrentStage}");
        builder.AppendLine($"[STAGE_SKILL] {stageSkill.SkillName}");
        builder.AppendLine($"[STAGE_TARGET] {stageSkill.Description}");
        builder.AppendLine($"[REQUIRED_FIELDS] {string.Join(", ", stageSkill.RequiredFields)}");
        builder.AppendLine($"[MISSING_FIELDS] {(missingFields.Count == 0 ? "none" : string.Join(", ", missingFields))}");
        builder.AppendLine("[REPLY_RULES]");
        builder.AppendLine("1. 使用简体中文回复，聚焦当前阶段信息收集。");
        builder.AppendLine("2. 先确认已收集信息，再追问缺失字段。");
        builder.AppendLine("3. 不要编造事实，未知项直接说明待补充。");
        builder.AppendLine("4. 当前阶段字段齐全时，提示可进入下一阶段。");
        builder.AppendLine();
        builder.AppendLine("[USER_INPUT]");
        builder.AppendLine(content);

        if (structuredAnswers is null || structuredAnswers.Count == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("[STRUCTURED_ANSWERS]");
        foreach (var pair in structuredAnswers.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("- ");
            builder.Append(pair.Key);
            builder.Append(": ");
            builder.AppendLine(pair.Value);
        }

        return builder.ToString();
    }

    private static async Task<int> CountAssistantTurnsAsync(
        GatewayAppRuntime runtime,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await runtime.SessionManager.LoadAsync(sessionId, cancellationToken);
        if (session is null || session.History.Count == 0)
        {
            return 0;
        }

        return session.History.Count(static x => string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ChatTurn?> WaitForAssistantTurnAsync(
        GatewayAppRuntime runtime,
        string sessionId,
        int baselineAssistantCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await runtime.SessionManager.LoadAsync(sessionId, cancellationToken);
            if (session?.History is { Count: > 0 } history)
            {
                var assistantTurns = history
                    .Where(static x => string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (assistantTurns.Length > baselineAssistantCount)
                {
                    return assistantTurns[^1];
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return null;
    }

    private static StagePreview BuildStagePreview(
        HireBotState state,
        IReadOnlyDictionary<string, string>? structuredAnswers,
        string assistantSummary)
    {
        var stageSkill = ResolveStageSkillMapping(state.CurrentStage);
        var structuredData = new Dictionary<string, string?>(state.CollectedFields, StringComparer.OrdinalIgnoreCase);
        if (structuredAnswers is not null)
        {
            foreach (var pair in structuredAnswers)
            {
                structuredData[pair.Key] = pair.Value;
            }
        }

        var missing = ResolveMissingFields(stageSkill, structuredData);
        var collectionReady = IsCollectionReadyForFinalize(structuredData);
        var riskNotes = missing.Count == 0
            ? collectionReady
                ? new[] { "全部阶段字段已收集完成，可执行 finalize 生成交付包。" }
                : new[] { "当前阶段字段已齐全，可继续下一阶段信息收集。" }
            : new[] { $"当前阶段仍缺少字段：{string.Join("、", missing)}" };

        return new StagePreview(
            HireId: state.HireId,
            Stage: state.CurrentStage,
            SkillName: stageSkill.SkillName,
            Summary: assistantSummary.Length > 240 ? assistantSummary[..240] : assistantSummary,
            StructuredData: structuredData,
            MissingFields: missing,
            RiskNotes: riskNotes,
            ReadyForAudit: missing.Count == 0,
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    private static StageSkillMapping ResolveStageSkillMapping(string stage)
    {
        return StageSkills.FirstOrDefault(skill =>
                   string.Equals(skill.Stage, stage, StringComparison.OrdinalIgnoreCase))
               ?? StageSkills[0];
    }

    private static IReadOnlyList<string> ResolveMissingFields(
        StageSkillMapping stageSkill,
        IReadOnlyDictionary<string, string?> structuredData)
    {
        var missing = new List<string>();
        foreach (var field in stageSkill.RequiredFields)
        {
            if (!structuredData.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missing.Add(field);
            }
        }

        return missing;
    }

    private static string ResolveCurrentStage(IReadOnlyDictionary<string, string?> structuredData)
    {
        foreach (var stageSkill in StageSkills)
        {
            if (ResolveMissingFields(stageSkill, structuredData).Count > 0)
            {
                return stageSkill.Stage;
            }
        }

        return HireStages.Package;
    }

    private static bool IsCollectionReadyForFinalize(IReadOnlyDictionary<string, string?> structuredData)
    {
        return StageSkills.All(stageSkill => ResolveMissingFields(stageSkill, structuredData).Count == 0);
    }

    private static string ComputeDigest(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static Dictionary<string, byte[]> BuildArtifactFiles(HireBotState state, Session? session)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["hire-metadata.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                state.HireId,
                state.TemplateId,
                state.TenantId,
                state.OperatorId,
                state.OwnerSubject,
                state.SandboxId,
                state.SessionId,
                state.CreatedAtUtc,
                state.UpdatedAtUtc,
                state.CurrentStage,
                state.CollectionPhase
            }, JsonOptions)),
            ["conversation-timeline.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Messages, JsonOptions)),
            ["collected-fields.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.CollectedFields, JsonOptions)),
            ["stage-preview.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.LatestPreview, JsonOptions)),
            ["audit-logs.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.AuditLogs, JsonOptions))
        };

        if (session is not null)
        {
            result["runtime-session.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                session.Id,
                session.ChannelId,
                session.SenderId,
                session.CreatedAt,
                session.LastActiveAt,
                session.State,
                History = session.History
            }, JsonOptions));
        }

        var markdown = BuildHandoverMarkdown(state);
        result["handover.md"] = Encoding.UTF8.GetBytes(markdown);
        return result;
    }

    private static string BuildHandoverMarkdown(HireBotState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# HireBot 交付摘要");
        builder.AppendLine($"- HireId: {state.HireId}");
        builder.AppendLine($"- SandboxId: {state.SandboxId}");
        builder.AppendLine($"- SessionId: {state.SessionId}");
        builder.AppendLine($"- Owner: {state.OwnerSubject}");
        builder.AppendLine($"- CollectionPhase: {state.CollectionPhase}");
        builder.AppendLine($"- CurrentStage: {state.CurrentStage}");
        builder.AppendLine();
        builder.AppendLine("## 已收集字段");
        if (state.CollectedFields.Count == 0)
        {
            builder.AppendLine("- 暂无结构化字段");
        }
        else
        {
            foreach (var pair in state.CollectedFields.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ");
                builder.Append(pair.Key);
                builder.Append(": ");
                builder.AppendLine(pair.Value ?? string.Empty);
            }
        }

        return builder.ToString();
    }

    private static byte[] BuildZipArchive(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in files)
            {
                var entry = zip.CreateEntry(pair.Key, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }
        }

        return ms.ToArray();
    }

    private static async Task PersistConversationToSandboxAsync(
        HireBotState state,
        GatewayConfig config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionConfig = BuildConnectionConfig(config);
            var readyTimeoutSeconds = ResolveSandboxReadyTimeoutSeconds(config);
            await using var sandbox = await Sandbox.ConnectAsync(new SandboxConnectOptions
            {
                SandboxId = state.SandboxId,
                ConnectionConfig = connectionConfig,
                ReadyTimeoutSeconds = readyTimeoutSeconds
            }, cancellationToken);

            var data = new
            {
                state.HireId,
                state.TemplateId,
                state.TenantId,
                state.OperatorId,
                state.OwnerSubject,
                state.SandboxId,
                state.SessionId,
                state.Status,
                state.CollectionPhase,
                state.CurrentStage,
                state.CollectedFields,
                state.UpdatedAtUtc
            };

            await sandbox.Files.CreateDirectoriesAsync(
                [
                    new CreateDirectoryEntry { Path = state.VolumePath, Mode = 755 }
                ],
                cancellationToken);

            await sandbox.Files.WriteFilesAsync(
                [
                    new WriteEntry
                    {
                        Path = $"{state.VolumePath}/conversation-state.json",
                        Data = JsonSerializer.Serialize(data, JsonOptions),
                        Mode = 644
                    },
                    new WriteEntry
                    {
                        Path = $"{state.VolumePath}/conversation-timeline.json",
                        Data = JsonSerializer.Serialize(state.Messages, JsonOptions),
                        Mode = 644
                    }
                ],
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 HireBot 数据卷失败: HireId={HireId}, SandboxId={SandboxId}", state.HireId, state.SandboxId);
        }

        await PersistSnapshotToDiskAsync(state, config, logger, cancellationToken);
    }

    private static async Task PersistSnapshotToDiskAsync(
        HireBotState state,
        GatewayConfig config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            HireBotStateSnapshot snapshot;
            lock (state.SyncRoot)
            {
                snapshot = HireBotStateSnapshot.FromState(state);
            }

            var snapshotPath = BuildSnapshotFilePath(config, state.HireId);
            var snapshotDirectory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(snapshotDirectory))
            {
                Directory.CreateDirectory(snapshotDirectory);
            }

            var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(snapshotPath, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 HireBot 状态快照失败: HireId={HireId}", state.HireId);
        }
    }

    private static async Task PersistArtifactsToSandboxAsync(
        HireBotState state,
        GatewayConfig config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (state.ArtifactFiles.Count == 0)
        {
            return;
        }

        try
        {
            var connectionConfig = BuildConnectionConfig(config);
            var readyTimeoutSeconds = ResolveSandboxReadyTimeoutSeconds(config);
            await using var sandbox = await Sandbox.ConnectAsync(new SandboxConnectOptions
            {
                SandboxId = state.SandboxId,
                ConnectionConfig = connectionConfig,
                ReadyTimeoutSeconds = readyTimeoutSeconds
            }, cancellationToken);

            var artifactRoot = $"{state.VolumePath}/artifacts";
            await sandbox.Files.CreateDirectoriesAsync(
                [
                    new CreateDirectoryEntry { Path = artifactRoot, Mode = 755 }
                ],
                cancellationToken);

            var entries = state.ArtifactFiles
                .Select(pair => new WriteEntry
                {
                    Path = $"{artifactRoot}/{pair.Key}",
                    Data = Encoding.UTF8.GetString(pair.Value),
                    Mode = 644
                })
                .ToArray();

            await sandbox.Files.WriteFilesAsync(entries, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 HireBot 交付包到数据卷失败: HireId={HireId}", state.HireId);
        }
    }

    private sealed class HireBotState
    {
        public required string HireId { get; init; }
        public required string TemplateId { get; init; }
        public required string TenantId { get; init; }
        public required string OperatorId { get; init; }
        public required string OwnerSubject { get; init; }
        public required string SandboxId { get; init; }
        public required string SessionId { get; init; }
        public required string ChannelId { get; init; }
        public required string SenderId { get; init; }
        public required string VolumePath { get; init; }
        public required string Status { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public required string CollectionPhase { get; set; }
        public required string CurrentStage { get; set; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset UpdatedAtUtc { get; set; }
        public List<ConversationMessage> Messages { get; } = [];
        public Dictionary<string, string?> CollectedFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public StagePreview? LatestPreview { get; set; }
        public List<AuditLog> AuditLogs { get; } = [];
        public Dictionary<string, byte[]> ArtifactFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? ArtifactArchive { get; set; }
        public string? ArtifactArchiveFileName { get; set; }
        public object SyncRoot { get; } = new();
    }

    private sealed class HireBotStateSnapshot
    {
        public required string HireId { get; init; }
        public required string TemplateId { get; init; }
        public required string TenantId { get; init; }
        public required string OperatorId { get; init; }
        public required string OwnerSubject { get; init; }
        public required string SandboxId { get; init; }
        public required string SessionId { get; init; }
        public required string ChannelId { get; init; }
        public required string SenderId { get; init; }
        public required string VolumePath { get; init; }
        public required string Status { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public required string CollectionPhase { get; init; }
        public required string CurrentStage { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset UpdatedAtUtc { get; init; }
        public List<ConversationMessage> Messages { get; init; } = [];
        public Dictionary<string, string?> CollectedFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public StagePreview? LatestPreview { get; init; }
        public List<AuditLog> AuditLogs { get; init; } = [];
        public Dictionary<string, byte[]> ArtifactFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? ArtifactArchive { get; init; }
        public string? ArtifactArchiveFileName { get; init; }

        public static HireBotStateSnapshot FromState(HireBotState state)
        {
            return new HireBotStateSnapshot
            {
                HireId = state.HireId,
                TemplateId = state.TemplateId,
                TenantId = state.TenantId,
                OperatorId = state.OperatorId,
                OwnerSubject = state.OwnerSubject,
                SandboxId = state.SandboxId,
                SessionId = state.SessionId,
                ChannelId = state.ChannelId,
                SenderId = state.SenderId,
                VolumePath = state.VolumePath,
                Status = state.Status,
                ErrorCode = state.ErrorCode,
                ErrorMessage = state.ErrorMessage,
                CollectionPhase = state.CollectionPhase,
                CurrentStage = state.CurrentStage,
                CreatedAtUtc = state.CreatedAtUtc,
                UpdatedAtUtc = state.UpdatedAtUtc,
                Messages = state.Messages.ToList(),
                CollectedFields = state.CollectedFields.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                LatestPreview = state.LatestPreview,
                AuditLogs = state.AuditLogs.ToList(),
                ArtifactFiles = state.ArtifactFiles.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                ArtifactArchive = state.ArtifactArchive?.ToArray(),
                ArtifactArchiveFileName = state.ArtifactArchiveFileName
            };
        }

        public HireBotState ToState()
        {
            var state = new HireBotState
            {
                HireId = HireId,
                TemplateId = TemplateId,
                TenantId = TenantId,
                OperatorId = OperatorId,
                OwnerSubject = OwnerSubject,
                SandboxId = SandboxId,
                SessionId = SessionId,
                ChannelId = ChannelId,
                SenderId = SenderId,
                VolumePath = VolumePath,
                Status = Status,
                ErrorCode = ErrorCode,
                ErrorMessage = ErrorMessage,
                CollectionPhase = CollectionPhase,
                CurrentStage = CurrentStage,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedAtUtc = UpdatedAtUtc,
                LatestPreview = LatestPreview,
                ArtifactArchive = ArtifactArchive?.ToArray(),
                ArtifactArchiveFileName = ArtifactArchiveFileName
            };

            if (Messages.Count > 0)
            {
                state.Messages.AddRange(Messages);
            }

            foreach (var pair in CollectedFields)
            {
                state.CollectedFields[pair.Key] = pair.Value;
            }

            if (AuditLogs.Count > 0)
            {
                state.AuditLogs.AddRange(AuditLogs);
            }

            foreach (var pair in ArtifactFiles)
            {
                state.ArtifactFiles[pair.Key] = pair.Value.ToArray();
            }

            return state;
        }
    }

    private static class HireStatuses
    {
        public const string Ready = "READY";
    }

    private static class HireCollectionPhases
    {
        public const string NotStarted = "NOT_STARTED";
        public const string InProgress = "IN_PROGRESS";
        public const string ReadyForFinalize = "READY_FOR_FINALIZE";
        public const string Finalized = "FINALIZED";
    }

    private static class HireStages
    {
        public const string Goal = "GOAL";
        public const string Scenario = "SCENARIO";
        public const string Systems = "SYSTEMS";
        public const string Gaps = "GAPS";
        public const string Package = "PACKAGE";
        public const string Done = "DONE";
    }

    private sealed record HireTemplateRequest(string TemplateId, string TenantId, string OperatorId, string? UseCase);
    private sealed record HireTemplateResult(string HireId, string SandboxId, string Status, string NextAction);
    private sealed record HireStatusResult(string HireId, string SandboxId, string Status, string? ErrorCode, string? ErrorMessage, string CollectionPhase, string CurrentStage);
    private sealed record StartConversationResult(string HireId, string SessionId, string CurrentStage, bool RequiresAudit, IReadOnlyList<StageSkillMapping> StageSkills);
    private sealed record ConversationMessageRequest(string Content, IReadOnlyDictionary<string, string>? StructuredAnswers);
    private sealed record ConversationResult(string HireId, string SessionId, string CurrentStage, bool RequiresAudit, ConversationMessage AssistantMessage, StagePreview LatestPreview);
    private sealed record ConversationTimelineResult(string HireId, string SessionId, string CurrentStage, bool RequiresAudit, string CollectionPhase, IReadOnlyList<ConversationMessage> Messages, IReadOnlyList<StageSkillMapping> StageSkills);
    private sealed record AuditDecisionRequest(string Stage, string Decision, string? Comment, string? RollbackTargetStage);
    private sealed record AuditDecisionResult(string HireId, string Stage, string Decision, string CurrentStage, bool RequiresAudit, string CollectionPhase);
    private sealed record FinalizeResult(string HireId, string CurrentStage, string CollectionPhase, IReadOnlyList<string> GeneratedFiles, string DownloadUrl);
    private sealed record WorkflowStateResult(string HireId, string SessionId, string CurrentStage, bool RequiresAudit, string CollectionPhase, IReadOnlyList<StageSkillMapping> StageSkills, IReadOnlyList<AuditLog> AuditLogs);
    private sealed record StageSkillMapping(string Stage, string SkillName, IReadOnlyList<string> RequiredFields, string Description);
    private sealed record ConversationMessage(string MessageId, string Role, string Content, DateTimeOffset CreatedAt);
    private sealed record StagePreview(
        string HireId,
        string Stage,
        string SkillName,
        string Summary,
        IReadOnlyDictionary<string, string?> StructuredData,
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<string> RiskNotes,
        bool ReadyForAudit,
        DateTimeOffset GeneratedAt);
    private sealed record AuditLog(
        string LogId,
        string Stage,
        string SkillName,
        string Decision,
        string Actor,
        string? Comment,
        string InputDigest,
        string OutputDigest,
        DateTimeOffset TimestampUtc);
}

