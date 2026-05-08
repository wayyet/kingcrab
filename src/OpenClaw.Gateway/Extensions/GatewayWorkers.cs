using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

internal static class GatewayWorkers
{
    public static void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        bool isNonLoopbackBind,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        IAgentRuntime agentRuntime,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        GatewayConfig config,
        CronScheduler? cronScheduler,
        HeartbeatService heartbeatService,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations,
        RuntimeMetrics? runtimeMetrics = null,
        LearningService? learningService = null,
        GatewayAutomationService? automationService = null,
        ContractGovernanceService? contractGovernance = null,
        MediaCacheStore? mediaCache = null,
        SessionAbortRegistry? abortRegistry = null)
    {
        StartSessionCleanup(lifetime, logger, sessionManager, sessionLocks, lockLastUsed);
        StartInboundWorkers(lifetime, logger, workerCount, isNonLoopbackBind, sessionManager, sessionLocks, lockLastUsed, pipeline, middlewarePipeline, wsChannel, agentRuntime, channelAdapters, config, cronScheduler, heartbeatService, toolApprovalService, approvalAuditStore, pairingManager, commandProcessor, operations, runtimeMetrics, learningService, automationService, contractGovernance, mediaCache, abortRegistry);
        StartOutboundWorkers(lifetime, logger, workerCount, pipeline, channelAdapters, heartbeatService);
    }

    private static void StartSessionCleanup(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
            {
                try 
                {
                    var evicted = sessionManager.SweepExpiredActiveSessions();
                    if (evicted > 0)
                        logger.LogDebug("Proactive active-session sweep evicted {Count} expired sessions", evicted);

                    sessionManager.CleanupSessionLocksOnce(DateTimeOffset.UtcNow, TimeSpan.FromHours(2));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during session lock cleanup");
                }
            }
        }, lifetime.ApplicationStopping);
    }

    internal static void CleanupSessionLocksOnce(
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        DateTimeOffset now,
        TimeSpan orphanThreshold,
        ILogger logger)
    {
        foreach (var kvp in sessionLocks)
        {
            var sessionKey = kvp.Key;
            var semaphore = kvp.Value;

            lockLastUsed.TryAdd(sessionKey, now);

            if (sessionManager.IsActive(sessionKey))
            {
                lockLastUsed[sessionKey] = now;
                continue;
            }

            var lastUsed = lockLastUsed.GetValueOrDefault(sessionKey, now);
            var isOrphaned = (now - lastUsed) > orphanThreshold;
            if (!isOrphaned || semaphore.CurrentCount != 1 || !semaphore.Wait(0))
                continue;

            var removed = false;
            try
            {
                if (sessionManager.IsActive(sessionKey))
                {
                    lockLastUsed[sessionKey] = now;
                    continue;
                }

                if (sessionLocks.TryRemove(sessionKey, out var removedSemaphore))
                {
                    removed = true;
                    lockLastUsed.TryRemove(sessionKey, out _);
                    try { removedSemaphore.Release(); } catch { }
                    removedSemaphore.Dispose();
                    logger.LogDebug("Cleaned up session lock for {SessionKey}", sessionKey);
                }
            }
            finally
            {
                if (!removed)
                {
                    try { semaphore.Release(); } catch { }
                }
            }
        }
    }

    internal static void DisposeSessionLocks(
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ILogger logger)
    {
        foreach (var sessionKey in sessionLocks.Keys)
        {
            if (!sessionLocks.TryRemove(sessionKey, out var semaphore))
                continue;

            try
            {
                semaphore.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to dispose session lock for {SessionKey}", sessionKey);
            }
        }
    }

    private static void StartInboundWorkers(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        bool isNonLoopbackBind,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        IAgentRuntime agentRuntime,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        GatewayConfig config,
        CronScheduler? cronScheduler,
        HeartbeatService heartbeatService,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations,
        RuntimeMetrics? runtimeMetrics,
        LearningService? learningService,
        GatewayAutomationService? automationService,
        ContractGovernanceService? contractGovernance,
        MediaCacheStore? mediaCache = null,
        SessionAbortRegistry? abortRegistry = null)
    {
        var routeResolver = config.Routing.Enabled
            ? new OpenClaw.Gateway.Integrations.AgentRouteResolver(config.Routing)
            : null;

        for (var i = 0; i < workerCount; i++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.InboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                        while (pipeline.InboundReader.TryRead(out var msg))
                        {
                            Session? session = null;
                            IAsyncDisposable? sessionLock = null;
                            CancellationTokenSource? abortCts = null;
                            IBridgedChannelControl? bridgedAdapter = null;
                            var bridgedTypingStarted = false;
                            long initialInputTokens = 0;
                            long initialOutputTokens = 0;
                            var cronRunOutcome = "failed";
                            string? cronRunPreview = null;
                            var conversationRecipientId = ResolveConversationRecipientId(msg);
                            using var processingCts = CreateProcessingCts(msg.RequestCancellation, lifetime.ApplicationStopping);
                            var processingCt = processingCts?.Token ?? lifetime.ApplicationStopping;
                            try
                            {
                                if (!msg.IsSystem)
                                {
                                    if (!operations.ActorRateLimits.TryConsume("channel_sender", $"{msg.ChannelId}:{msg.SenderId}", "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = "Rate limit exceeded. Please slow down.",
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                    continue;
                                }

                                var effectiveSessionKey = msg.SessionId ?? $"{msg.ChannelId}:{conversationRecipientId}";
                                if (!operations.ActorRateLimits.TryConsume("session", effectiveSessionKey, "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = "Session rate limit exceeded. Please retry shortly.",
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                    continue;
                                }
                            }

                            // ── Tool Approval Decision Short-Circuit ────────────
                            if (string.Equals(msg.Type, "tool_approval_decision", StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(msg.ApprovalId) &&
                                msg.Approved is not null)
                            {
                                var decisionOutcome = toolApprovalService.TrySetDecisionWithRequest(
                                    msg.ApprovalId,
                                    msg.Approved.Value,
                                    msg.ChannelId,
                                    msg.SenderId,
                                    requireRequesterMatch: true);

                                if (decisionOutcome.Result == ToolApprovalDecisionResult.Recorded && decisionOutcome.Request is not null)
                                {
                                    runtimeMetrics?.IncrementApprovalDecisionsRecorded();
                                    approvalAuditStore.RecordDecision(
                                        decisionOutcome.Request,
                                        msg.Approved.Value,
                                        "chat",
                                        msg.ChannelId,
                                        msg.SenderId);
                                    RecordApprovalDecisionEvent(
                                        operations,
                                        decisionOutcome.Request,
                                        msg.Approved.Value,
                                        "chat",
                                        msg.ChannelId,
                                        msg.SenderId);
                                }
                                else if (decisionOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                                {
                                    runtimeMetrics?.IncrementApprovalDecisionsRejected();
                                    RecordApprovalDecisionRejectedEvent(
                                        operations,
                                        decisionOutcome.Request,
                                        msg.ApprovalId,
                                        "requester_mismatch",
                                        msg.ChannelId,
                                        msg.SenderId);
                                }

                                var ack = decisionOutcome.Result switch
                                {
                                    ToolApprovalDecisionResult.Recorded => $"Tool approval recorded: {msg.ApprovalId} = {(msg.Approved.Value ? "approved" : "denied")}",
                                    ToolApprovalDecisionResult.Unauthorized => $"Approval id is not valid for this sender/channel: {msg.ApprovalId}",
                                    _ => $"No pending approval found for id: {msg.ApprovalId}"
                                };

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = conversationRecipientId,
                                    AccountId = msg.AccountId,
                                    Text = ack,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);

                                continue;
                            }

                            // Text fallback: "/approve <approvalId> yes|no"
                            if (!string.IsNullOrWhiteSpace(msg.Text) && msg.Text.StartsWith("/approve ", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = msg.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 3)
                                {
                                    var approvalId = parts[1];
                                    var decision = parts[2];
                                    var approved = decision.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("y", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("approve", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("true", StringComparison.OrdinalIgnoreCase);
                                    var denied = decision.Equals("no", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("n", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("deny", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("false", StringComparison.OrdinalIgnoreCase);

                                    if (approved || denied)
                                    {
                                        var decisionOutcome = toolApprovalService.TrySetDecisionWithRequest(
                                            approvalId,
                                            approved,
                                            msg.ChannelId,
                                            msg.SenderId,
                                            requireRequesterMatch: true);

                                    if (decisionOutcome.Result == ToolApprovalDecisionResult.Recorded && decisionOutcome.Request is not null)
                                    {
                                        runtimeMetrics?.IncrementApprovalDecisionsRecorded();
                                        approvalAuditStore.RecordDecision(
                                            decisionOutcome.Request,
                                            approved,
                                                "chat",
                                                msg.ChannelId,
                                                msg.SenderId);
                                            RecordApprovalDecisionEvent(
                                                operations,
                                                decisionOutcome.Request,
                                                approved,
                                                "chat",
                                                msg.ChannelId,
                                                msg.SenderId);
                                    }
                                    else if (decisionOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                                    {
                                        runtimeMetrics?.IncrementApprovalDecisionsRejected();
                                        RecordApprovalDecisionRejectedEvent(
                                            operations,
                                            decisionOutcome.Request,
                                                approvalId,
                                                "requester_mismatch",
                                                msg.ChannelId,
                                                msg.SenderId);
                                        }

                                        var ack = decisionOutcome.Result switch
                                        {
                                            ToolApprovalDecisionResult.Recorded => $"Tool approval recorded: {approvalId} = {(approved ? "approved" : "denied")}",
                                            ToolApprovalDecisionResult.Unauthorized => $"Approval id is not valid for this sender/channel: {approvalId}",
                                            _ => $"No pending approval found for id: {approvalId}"
                                        };

                                        await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                        {
                                            ChannelId = msg.ChannelId,
                                            RecipientId = conversationRecipientId,
                                            AccountId = msg.AccountId,
                                            Text = ack,
                                            ReplyToMessageId = msg.MessageId
                                        }, lifetime.ApplicationStopping);

                                        continue;
                                    }
                                }
                            }

                            // ── DM Pairing Check ─────────────────────────────────
                            var policy = "open";
                            if (msg.ChannelId == "sms") policy = config.Channels.Sms.DmPolicy;
                            if (msg.ChannelId == "telegram") policy = config.Channels.Telegram.DmPolicy;
                            if (msg.ChannelId == "whatsapp") policy = config.Channels.WhatsApp.DmPolicy;
                            if (msg.ChannelId == "teams") policy = config.Channels.Teams.DmPolicy;
                            if (msg.ChannelId == "slack") policy = config.Channels.Slack.DmPolicy;
                            if (msg.ChannelId == "discord") policy = config.Channels.Discord.DmPolicy;
                            if (msg.ChannelId == "signal") policy = config.Channels.Signal.DmPolicy;

                            if (policy is "closed")
                                continue; // Silently drop all inbound messages

                            if (!msg.IsSystem && policy is "pairing" && !pairingManager.IsApproved(msg.ChannelId, msg.SenderId))
                            {
                                var code = pairingManager.GeneratePairingCode(msg.ChannelId, msg.SenderId);
                                var pairingMsg = $"Welcome to OpenClaw. Your pairing code is {code}. Your messages will be ignored until an admin approves this pair.";

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = conversationRecipientId,
                                    AccountId = msg.AccountId,
                                    Text = pairingMsg,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);

                                continue; // Drop the inbound request after sending pairing code
                            }

                            // ── Multi-Agent Route Resolution ─────────────────
                            var resolvedRoute = routeResolver?.Resolve(msg.ChannelId, msg.SenderId);

                            session = msg.SessionId is not null
                                ? await sessionManager.GetOrCreateByIdAsync(msg.SessionId, msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping)
                                : await sessionManager.GetOrCreateAsync(msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping);
                            if (session is null)
                                throw new InvalidOperationException("Session manager returned null session.");

                            // Refresh transient routing identity each turn. The persisted Session.SenderId may
                            // hold a stale connection id (e.g. WebSocket Connection.Id from a previous session
                            // load), which causes mid-turn envelope routing (artifact, stage gate) to silently
                            // drop because the dead id is no longer in the channel's connection table.
                            session.ChannelId = msg.ChannelId;
                            session.SenderId = conversationRecipientId;

                            // Apply cron job model override before route resolution (route can further override)
                            if (!string.IsNullOrWhiteSpace(msg.ModelOverride))
                                session.ModelOverride = msg.ModelOverride;

                            // Apply route overrides to session
                            if (resolvedRoute is not null)
                            {
                                session.ModelOverride = string.IsNullOrWhiteSpace(resolvedRoute.ModelOverride)
                                    ? session.ModelOverride
                                    : resolvedRoute.ModelOverride.Trim();
                                session.ModelProfileId = string.IsNullOrWhiteSpace(resolvedRoute.ModelProfileId)
                                    ? null
                                    : resolvedRoute.ModelProfileId.Trim();
                                session.PreferredModelTags = resolvedRoute.PreferredModelTags
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                session.FallbackModelProfileIds = resolvedRoute.FallbackModelProfileIds
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                session.ModelRequirements = resolvedRoute.ModelRequirements ?? new ModelSelectionRequirements();
                                session.SystemPromptOverride = string.IsNullOrWhiteSpace(resolvedRoute.SystemPrompt)
                                    ? null
                                    : resolvedRoute.SystemPrompt.Trim();
                                session.RoutePresetId = string.IsNullOrWhiteSpace(resolvedRoute.PresetId)
                                    ? null
                                    : resolvedRoute.PresetId.Trim();
                                session.RouteAllowedTools = resolvedRoute.AllowedTools
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                            }
                            else
                            {
                                session.ModelProfileId = null;
                                session.PreferredModelTags = [];
                                session.FallbackModelProfileIds = [];
                                session.ModelRequirements = new ModelSelectionRequirements();
                                session.SystemPromptOverride = null;
                                session.RoutePresetId = null;
                                session.RouteAllowedTools = [];
                            }

                            initialInputTokens = session.TotalInputTokens;
                            initialOutputTokens = session.TotalOutputTokens;

                            // ── Abort Intercept (pre-lock) ───────────────────────────────
                            // Must run BEFORE AcquireSessionLockAsync so a /stop command
                            // from any channel (Telegram, WhatsApp, Discord, WebSocket…)
                            // signals cancellation to the in-flight execution that holds the
                            // lock, rather than queuing behind it and arriving too late.
                            // If no execution is active TryAbort returns false and the message
                            // falls through to the command processor for a graceful reply.
                            if (!msg.IsSystem && abortRegistry is not null && IsAbortCommand(msg.Text))
                            {
                                if (abortRegistry.TryAbort(session.Id))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = "Stopped.",
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                    continue;
                                }
                                // No in-flight execution — fall through to command processor.
                            }

                            sessionLock = await sessionManager.AcquireSessionLockAsync(session.Id, processingCt);

                            // Register abort CTS so an admin can cancel this execution externally.
                            abortCts = abortRegistry?.Register(session.Id, processingCt);
                            var executionCt = abortCts?.Token ?? processingCt;

                            // ── Chat Command Processing ──────────────────────
                            var (handled, cmdResponse) = await commandProcessor.TryProcessCommandAsync(session, msg.Text, processingCt, sessionLockHeld: true);
                            if (handled)
                            {
                                if (cmdResponse is not null)
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = cmdResponse,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }
                                continue; // Skip LLM completely
                            }

                            var mwContext = new MessageContext
                            {
                                ChannelId = msg.ChannelId,
                                SenderId = msg.SenderId,
                                Text = msg.Text,
                                MessageId = msg.MessageId,
                                SessionId = session.Id,
                                SessionInputTokens = session.TotalInputTokens,
                                SessionOutputTokens = session.TotalOutputTokens
                            };

                            var shouldProceed = await middlewarePipeline.ExecuteAsync(mwContext, processingCt);
                            if (!shouldProceed)
                            {
                                var shortCircuitText = mwContext.ShortCircuitResponse ?? "Request blocked.";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "assistant_message", shortCircuitText, msg.MessageId,
                                        processingCt);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = shortCircuitText,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }
                                continue;
                            }

                            var messageText = mwContext.Text;
                            if (!string.IsNullOrWhiteSpace(msg.MediaUrl) && !messageText.Contains("[IMAGE_URL:", StringComparison.Ordinal))
                            {
                                var marker = BuildMediaMarker(msg);
                                if (!string.IsNullOrWhiteSpace(marker))
                                    messageText = string.IsNullOrWhiteSpace(messageText) ? marker : $"{marker}\n{messageText}";
                            }

                            // Resolve [FILE_URL:/media/{id}] markers to [FILE_PATH:{diskPath}] so the
                            // agent can use read_file directly instead of trying an HTTP path.
                            if (mediaCache is not null && messageText.Contains("[FILE_URL:/media/", StringComparison.Ordinal))
                            {
                                messageText = await ResolveFileUrlMarkersAsync(messageText, mediaCache, processingCt);
                            }
                            var useStreaming = msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

                            var approvalTimeout = TimeSpan.FromSeconds(Math.Clamp(config.Tooling.ToolApprovalTimeoutSeconds, 5, 3600));
                            async ValueTask<bool> ApprovalCallback(string toolName, string argsJson, CancellationToken ct)
                            {
                                var actionDescriptor = ToolActionPolicyResolver.Resolve(toolName, argsJson);
                                var grant = operations.ApprovalGrants.TryConsume(session.Id, msg.ChannelId, msg.SenderId, toolName);
                                if (grant is not null)
                                {
                                    operations.RuntimeEvents.Append(new RuntimeEventEntry
                                    {
                                        Id = $"evt_{Guid.NewGuid():N}"[..20],
                                        SessionId = session.Id,
                                        ChannelId = msg.ChannelId,
                                        SenderId = msg.SenderId,
                                        Component = "approval",
                                        Action = "grant_consumed",
                                        Severity = "info",
                                        Summary = $"Reusable approval grant '{grant.Id}' applied for tool '{toolName}'.",
                                        Metadata = new Dictionary<string, string>
                                        {
                                            ["toolName"] = toolName,
                                            ["grantId"] = grant.Id,
                                            ["scope"] = grant.Scope
                                        }
                                    });
                                    return true;
                                }

                                var req = toolApprovalService.Create(
                                    session.Id,
                                    msg.ChannelId,
                                    msg.SenderId,
                                    toolName,
                                    argsJson,
                                    approvalTimeout,
                                    action: actionDescriptor.Action,
                                    isMutation: actionDescriptor.IsMutation,
                                    summary: actionDescriptor.Summary);
                                approvalAuditStore.RecordCreated(req);
                                operations.RuntimeEvents.Append(new RuntimeEventEntry
                                {
                                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                                    SessionId = session.Id,
                                    ChannelId = msg.ChannelId,
                                    SenderId = msg.SenderId,
                                    Component = "approval",
                                    Action = "requested",
                                    Severity = "info",
                                    Summary = string.IsNullOrWhiteSpace(actionDescriptor.Summary)
                                        ? $"Tool approval requested for '{toolName}'."
                                        : actionDescriptor.Summary,
                                    Metadata = new Dictionary<string, string>
                                    {
                                        ["toolName"] = toolName,
                                        ["approvalId"] = req.ApprovalId,
                                        ["action"] = actionDescriptor.Action,
                                        ["isMutation"] = actionDescriptor.IsMutation ? "true" : "false"
                                    }
                                });

                                var preview = argsJson.Length <= 800 ? argsJson : argsJson[..800] + "…";

                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                    {
                                        Type = "tool_approval_required",
                                        ApprovalId = req.ApprovalId,
                                        ToolName = toolName,
                                        ArgumentsPreview = preview,
                                        InReplyToMessageId = msg.MessageId,
                                        Text = string.IsNullOrWhiteSpace(req.Summary) ? "Tool approval required." : req.Summary
                                    }, ct);
                                }
                                else
                                {
                                    var prompt = $"Tool approval required.\n" +
                                                 $"- id: {req.ApprovalId}\n" +
                                                 $"- tool: {toolName}\n" +
                                                 $"{(string.IsNullOrWhiteSpace(req.Action) ? "" : $"- action: {req.Action}\n")}" +
                                                 $"{(string.IsNullOrWhiteSpace(req.Summary) ? "" : $"- summary: {req.Summary}\n")}" +
                                                 $"- args: {preview}\n\n" +
                                                 $"Reply with: /approve {req.ApprovalId} yes|no";

                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = prompt,
                                        ReplyToMessageId = msg.MessageId
                                    }, ct);
                                }

                                var outcome = await toolApprovalService.WaitForDecisionOutcomeAsync(req.ApprovalId, approvalTimeout, ct);
                                if (outcome.Result == ToolApprovalWaitResult.TimedOut && outcome.Request is not null)
                                {
                                    approvalAuditStore.RecordDecision(
                                        outcome.Request,
                                        approved: false,
                                        "timeout",
                                        actorChannelId: null,
                                        actorSenderId: null);
                                    RecordApprovalTimedOutEvent(operations, outcome.Request);
                                }

                                return outcome.Result == ToolApprovalWaitResult.Approved;
                            }

                            if (useStreaming)
                            {
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_start", "", msg.MessageId, processingCt);

                                AgentStreamEvent? doneEvent = null;
                                var streamHistoryCountBefore = session.History.Count;
                                await foreach (var evt in agentRuntime.RunStreamingAsync(
                                    session, messageText, executionCt, approvalCallback: ApprovalCallback, isSystemEvent: msg.IsSystem))
                                {
                                    if (string.Equals(evt.EnvelopeType, "assistant_done", StringComparison.Ordinal))
                                    {
                                        doneEvent = evt;
                                        continue;
                                    }

                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, evt.EnvelopeType, evt.Content, msg.MessageId,
                                        processingCt);
                                }

                                // Upload files written by write_file by scanning history (same as non-streaming path).
                                var streamFileUploads = new List<StoredMediaAsset>();
                                if (mediaCache is not null)
                                {
                                    for (var hi = streamHistoryCountBefore; hi < session.History.Count; hi++)
                                    {
                                        var hturn = session.History[hi];
                                        if (hturn.ToolCalls is null) continue;
                                        foreach (var call in hturn.ToolCalls)
                                        {
                                            if (call.Result is null || !call.Result.Contains("[FILE_PATH:", StringComparison.Ordinal))
                                                continue;
                                            var (fileMarkers, _) = MediaMarkerProtocol.Extract(call.Result);
                                            foreach (var marker in fileMarkers.Where(m => m.Kind == MediaMarkerKind.FilePath))
                                            {
                                                var fileAsset = await TryUploadFilePathAsync(marker.Value, config, mediaCache, processingCt);
                                                if (fileAsset is not null)
                                                    streamFileUploads.Add(fileAsset);
                                            }
                                        }
                                    }
                                }

                                // Inject FILE_URL markers into the last assistant ChatTurn BEFORE persisting
                                // so that history replays show download links via preprocessMediaMarkers.
                                if (streamFileUploads.Count > 0)
                                    InjectFileUrlMarkersIntoHistory(session, streamFileUploads);

                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

                                // Send verbose footer via stream for streaming sessions
                                if (session.VerboseMode)
                                {
                                    var streamInputDelta = session.TotalInputTokens - initialInputTokens;
                                    var streamOutputDelta = session.TotalOutputTokens - initialOutputTokens;
                                    var streamToolCalls = 0;
                                    for (var ti = session.History.Count - 1; ti >= 0; ti--)
                                    {
                                        var turn = session.History[ti];
                                        if (turn.ToolCalls is { Count: > 0 })
                                            streamToolCalls += turn.ToolCalls.Count;
                                        if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                                            break;
                                    }
                                    var verboseFooter = $"\n\n---\n{streamToolCalls} tool call(s) | {streamInputDelta} in / {streamOutputDelta} out tokens (this turn)";
                                    await wsChannel.SendStreamEventAsync(msg.SenderId, "text_delta", verboseFooter, msg.MessageId, processingCt);
                                }

                                if (doneEvent is AgentStreamEvent completedEvent)
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId,
                                        completedEvent.EnvelopeType,
                                        completedEvent.Content,
                                        msg.MessageId,
                                        processingCt);
                                }

                                // Deliver file_attachment envelopes for files written during this turn.
                                foreach (var fileAsset in streamFileUploads)
                                {
                                    await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                    {
                                        Type = "file_attachment",
                                        Text = fileAsset.FileName,
                                        FileUrl = $"/media/{fileAsset.Id}",
                                        FileName = fileAsset.FileName,
                                        MimeType = fileAsset.MediaType,
                                        FileSizeBytes = fileAsset.SizeBytes,
                                        InReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }

                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, processingCt);
                            }
                            else
                            {
                                // Send read receipt and typing indicator for bridged channels
                                bridgedAdapter = channelAdapters.TryGetValue(msg.ChannelId, out var adapter)
                                    ? adapter as IBridgedChannelControl : null;
                                var isSelfChat = bridgedAdapter?.SelfIds.Any(selfId =>
                                    string.Equals(selfId, msg.SenderId, StringComparison.Ordinal)) == true;

                                if (bridgedAdapter is not null && !isSelfChat)
                                {
                                    if (msg.MessageId is not null)
                                    {
                                        var receiptJid = msg.IsGroup ? msg.GroupId : msg.SenderId;
                                        var receiptParticipant = msg.IsGroup ? msg.SenderId : null;
                                        ObserveBackgroundTask(
                                            bridgedAdapter.SendReadReceiptAsync(msg.MessageId, receiptJid, receiptParticipant, msg.AccountId, processingCt).AsTask(),
                                            logger,
                                            "bridged read receipt");
                                    }
                                    ObserveBackgroundTask(
                                        bridgedAdapter.SendTypingAsync(conversationRecipientId, true, msg.AccountId, processingCt).AsTask(),
                                        logger,
                                        "bridged typing start");
                                    bridgedTypingStarted = true;
                                }

                                var historyCountBefore = session.History.Count;
                                var responseText = await agentRuntime.RunAsync(session, messageText, executionCt, approvalCallback: ApprovalCallback, isSystemEvent: msg.IsSystem);

                                // Upload files from write_file tool results and build asset list BEFORE persisting,
                                // so FILE_URL markers are injected into history for replay.
                                var nonStreamFileUploads = new List<StoredMediaAsset>();
                                if (mediaCache is not null)
                                {
                                    for (var hi = historyCountBefore; hi < session.History.Count; hi++)
                                    {
                                        var hturn = session.History[hi];
                                        if (hturn.ToolCalls is null) continue;
                                        foreach (var call in hturn.ToolCalls)
                                        {
                                            if (call.Result is null || !call.Result.Contains("[FILE_PATH:", StringComparison.Ordinal))
                                                continue;
                                            var (fileMarkers, _) = MediaMarkerProtocol.Extract(call.Result);
                                            foreach (var marker in fileMarkers.Where(m => m.Kind == MediaMarkerKind.FilePath))
                                            {
                                                var fileAsset = await TryUploadFilePathAsync(marker.Value, config, mediaCache, processingCt);
                                                if (fileAsset is not null)
                                                    nonStreamFileUploads.Add(fileAsset);
                                            }
                                        }
                                    }
                                }

                                // Append FILE_URL markers to history and responseText before persisting.
                                if (nonStreamFileUploads.Count > 0)
                                {
                                    InjectFileUrlMarkersIntoHistory(session, nonStreamFileUploads);
                                    // WebSocket envelope clients receive a dedicated file_attachment envelope below;
                                    // appending FILE_URL to responseText would produce a duplicate download link.
                                    if (!(msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId)))
                                        responseText += BuildFileUrlSuffix(nonStreamFileUploads);
                                }

                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

                                var inputTokenDelta = session.TotalInputTokens - initialInputTokens;
                                var outputTokenDelta = session.TotalOutputTokens - initialOutputTokens;
                                var suppressHeartbeatDelivery = heartbeatService.ShouldSuppressResult(msg.CronJobName, responseText);
                                if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                                    heartbeatService.RecordResult(session, responseText, suppressHeartbeatDelivery, inputTokenDelta, outputTokenDelta);

                                // Append verbose mode footer (tool calls and token delta)
                                if (session.VerboseMode)
                                {
                                    // Tool calls may be spread across multiple turns added during this run
                                    var turnToolCalls = 0;
                                    for (var ti = session.History.Count - 1; ti >= 0; ti--)
                                    {
                                        var turn = session.History[ti];
                                        if (turn.ToolCalls is { Count: > 0 })
                                            turnToolCalls += turn.ToolCalls.Count;
                                        // Stop once we reach a user turn (start of this interaction)
                                        if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                                            break;
                                    }
                                    responseText += $"\n\n---\n{turnToolCalls} tool call(s) | {inputTokenDelta} in / {outputTokenDelta} out tokens (this turn)";
                                }

                                // Append Usage Tracking string if configured
                                if (config.UsageFooter is "tokens")
                                    responseText += $"\n\n---\n↑ {session.TotalInputTokens} in / {session.TotalOutputTokens} out tokens";

                                // Deliver file_attachment envelopes for files uploaded this turn (WebSocket only).
                                if (nonStreamFileUploads.Count > 0 &&
                                    msg.ChannelId == "websocket" &&
                                    wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    foreach (var fileAsset in nonStreamFileUploads)
                                    {
                                        await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                        {
                                            Type = "file_attachment",
                                            Text = fileAsset.FileName,
                                            FileUrl = $"/media/{fileAsset.Id}",
                                            FileName = fileAsset.FileName,
                                            MimeType = fileAsset.MediaType,
                                            FileSizeBytes = fileAsset.SizeBytes,
                                            InReplyToMessageId = msg.MessageId
                                        }, processingCt);
                                    }
                                }

                                if (!suppressHeartbeatDelivery)
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = responseText,
                                        SessionId = session.Id,
                                        CronJobName = msg.CronJobName,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }

                                if (!string.IsNullOrWhiteSpace(msg.CronJobName))
                                {
                                    cronRunOutcome = "completed";
                                    cronRunPreview = responseText.Length > 200 ? responseText[..200] : responseText;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            if (session?.ContractPolicy is not null)
                                contractGovernance?.AppendSnapshot(session, "cancelled");

                            if (session is not null)
                                logger.LogWarning("Request canceled for session {SessionId}", session.Id);
                            else
                                logger.LogWarning("Request canceled for channel {ChannelId} sender {SenderId}", msg.ChannelId, msg.SenderId);
                        }
                        catch (Exception ex)
                        {
                            if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                            {
                                var inputTokenDelta = session is null ? 0 : session.TotalInputTokens - initialInputTokens;
                                var outputTokenDelta = session is null ? 0 : session.TotalOutputTokens - initialOutputTokens;
                                heartbeatService.RecordError(session, ex, inputTokenDelta, outputTokenDelta);
                            }

                            if (session is not null)
                                logger.LogError(ex, "Internal error processing message for session {SessionId}", session.Id);
                            else
                                logger.LogError(ex, "Internal error processing message for channel {ChannelId} sender {SenderId}", msg.ChannelId, msg.SenderId);

                            if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                                continue;

                            try
                            {
                                const string errorText = "Internal error.";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "error", errorText, msg.MessageId,
                                        processingCt);
                                        
                                    await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, processingCt);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = errorText,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }
                            }
                            catch { /* Best effort */ }
                        }
                        finally
                        {
                            if (bridgedAdapter is not null && bridgedTypingStarted)
                            {
                                try
                                {
                                    await bridgedAdapter.SendTypingAsync(conversationRecipientId, false, msg.AccountId, CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Background bridged typing stop failed");
                                }
                            }

                            cronScheduler?.MarkJobCompleted(msg.CronJobName);
                            automationService?.MarkRunCompleted(msg.CronJobName);

                            // Record run history for dynamic automations
                            if (automationService is not null && !string.IsNullOrWhiteSpace(msg.CronJobName)
                                && !heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                            {
                                try
                                {
                                    var inputDelta = session is null ? 0 : session.TotalInputTokens - initialInputTokens;
                                    var outputDelta = session is null ? 0 : session.TotalOutputTokens - initialOutputTokens;
                                    await automationService.AppendRunHistoryAsync(msg.CronJobName, new RunHistoryEntry
                                    {
                                        RanAtUtc = DateTimeOffset.UtcNow,
                                        Outcome = cronRunOutcome,
                                        InputTokens = inputDelta,
                                        OutputTokens = outputDelta,
                                        MessagePreview = cronRunPreview
                                    }, CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Failed to record run history for cron job '{JobName}'.", msg.CronJobName);
                                }
                            }

                            // Delete one-shot jobs that requested cleanup after running
                            if (msg.DeleteAfterRun && automationService is not null && !string.IsNullOrWhiteSpace(msg.CronJobName))
                            {
                                try
                                {
                                    await automationService.DeleteAsync(msg.CronJobName, CancellationToken.None);
                                    logger.LogInformation("One-shot cron job '{JobName}' deleted after run.", msg.CronJobName);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Failed to delete one-shot cron job '{JobName}' after run.", msg.CronJobName);
                                }
                            }

                            if (session is not null)
                                abortRegistry?.Unregister(session.Id);

                            if (sessionLock is not null)
                                await sessionLock.DisposeAsync();
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }

    private static void StartOutboundWorkers(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        MessagePipeline pipeline,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        HeartbeatService heartbeatService)
    {
        for (var j = 0; j < workerCount; j++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.OutboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                    while (pipeline.OutboundReader.TryRead(out var outbound))
                    {
                        if (!channelAdapters.TryGetValue(outbound.ChannelId, out var adapter))
                        {
                            logger.LogWarning("Unknown channel {ChannelId} for outbound message to {RecipientId}", outbound.ChannelId, outbound.RecipientId);
                            continue;
                        }

                        const int maxDeliveryAttempts = 2;
                        for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
                        {
                            try
                            {
                                await adapter.SendAsync(outbound, lifetime.ApplicationStopping);
                                if (heartbeatService.IsManagedHeartbeatJob(outbound.CronJobName))
                                    heartbeatService.RecordDeliverySucceeded(outbound.SessionId);
                                break;
                            }
                            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                if (attempt < maxDeliveryAttempts)
                                {
                                    logger.LogWarning(ex, "Outbound send failed for channel {ChannelId}, retrying…", outbound.ChannelId);
                                    await Task.Delay(500, lifetime.ApplicationStopping);
                                }
                                else
                                {
                                    logger.LogError(ex, "Outbound send failed for channel {ChannelId} after {Attempts} attempts", outbound.ChannelId, maxDeliveryAttempts);
                                }
                            }
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }

    private static CancellationTokenSource? CreateProcessingCts(CancellationToken requestCancellation, CancellationToken appStopping)
    {
        if (!requestCancellation.CanBeCanceled || requestCancellation == appStopping)
            return null;

        return CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, appStopping);
    }

    private static void ObserveBackgroundTask(Task task, ILogger logger, string operation)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    var (log, op) = ((ILogger, string))state!;
                    log.LogWarning(completed.Exception.GetBaseException(), "Background {Operation} failed", op);
                }
            },
            state: (logger, operation),
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default);
    }

    private static string ResolveConversationRecipientId(InboundMessage msg)
        => msg.IsGroup && !string.IsNullOrWhiteSpace(msg.GroupId)
            ? msg.GroupId!
            : msg.SenderId;

    private static bool IsAbortCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return string.Equals(t, "/stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "/cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "/abort", StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordApprovalDecisionEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest request,
        bool approved,
        string decisionSource,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = request.ApprovalId,
            ["toolName"] = request.ToolName,
            ["approved"] = approved ? "true" : "false",
            ["decisionSource"] = decisionSource
        };

        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "decision_recorded",
            Severity = "info",
            Summary = $"{decisionSource} {(approved ? "approved" : "denied")} tool approval '{request.ApprovalId}'.",
            Metadata = metadata
        });
    }

    private static void RecordApprovalDecisionRejectedEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest? request,
        string approvalId,
        string reason,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = approvalId,
            ["reason"] = reason
        };

        if (request is not null)
            metadata["toolName"] = request.ToolName;
        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request?.SessionId,
            ChannelId = request?.ChannelId,
            SenderId = request?.SenderId,
            Component = "approval",
            Action = "decision_rejected",
            Severity = "warning",
            Summary = $"Rejected approval decision attempt for '{approvalId}'.",
            Metadata = metadata
        });
    }

    private static void RecordApprovalTimedOutEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest request)
    {
        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "timed_out",
            Severity = "warning",
            Summary = $"Tool approval timed out for '{request.ToolName}'.",
            Metadata = new Dictionary<string, string>
            {
                ["approvalId"] = request.ApprovalId,
                ["toolName"] = request.ToolName
            }
        });
    }

    private static string? BuildMediaMarker(InboundMessage message)
        => (message.MediaType ?? "").ToLowerInvariant() switch
        {
            "image" => $"[IMAGE_URL:{message.MediaUrl}]",
            "audio" => $"[AUDIO_URL:{message.MediaUrl}]",
            "video" => $"[VIDEO_URL:{message.MediaUrl}]",
            "document" or "file" => $"[FILE_URL:{message.MediaUrl}]",
            _ => null
        };

    // Resolves [FILE_URL:/media/{id}] markers to [FILE_PATH:{diskPath}] so the agent
    // can read the file directly via the read_file tool.
    private static async Task<string> ResolveFileUrlMarkersAsync(string text, MediaCacheStore mediaCache, CancellationToken ct)
    {
        // Fast path: no relevant markers.
        if (!text.Contains("[FILE_URL:/media/", StringComparison.Ordinal))
            return text;

        var result = new System.Text.StringBuilder(text.Length);
        const string prefix = "[FILE_URL:/media/";
        var searchFrom = 0;

        while (true)
        {
            var startIdx = text.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                result.Append(text, searchFrom, text.Length - searchFrom);
                break;
            }

            result.Append(text, searchFrom, startIdx - searchFrom);

            var closeIdx = text.IndexOf(']', startIdx + prefix.Length);
            if (closeIdx < 0)
            {
                result.Append(text, startIdx, text.Length - startIdx);
                break;
            }

            // Extract the media ID: everything between "/media/" and "]"
            var mediaId = text.Substring(startIdx + prefix.Length, closeIdx - (startIdx + prefix.Length));

            // Only resolve IDs that look like our generated ones (no path separators).
            if (!mediaId.Contains('/') && !mediaId.Contains('\\') && !mediaId.Contains('.'))
            {
                var asset = await mediaCache.GetAsync(mediaId, ct);
                if (asset is not null && File.Exists(asset.Path))
                {
                    result.Append($"[FILE_PATH:{asset.Path}]");
                    searchFrom = closeIdx + 1;
                    continue;
                }
            }

            // Leave unchanged if not resolvable.
            result.Append(text, startIdx, closeIdx + 1 - startIdx);
            searchFrom = closeIdx + 1;
        }

        return result.ToString();
    }

    // Reads a file from disk (validating it is within an allowed write root) and saves it
    // to the media cache, returning the stored asset. Returns null if the file does not
    // exist or the path is not within an allowed write root.
    private static async Task<StoredMediaAsset?> TryUploadFilePathAsync(
        string filePath,
        GatewayConfig config,
        MediaCacheStore mediaCache,
        CancellationToken ct)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);

            // Validate the path is within an allowed write root.
            var roots = config.Tooling.AllowedWriteRoots;
            if (roots.Length == 0)
                return null;

            var pathAllowed = roots.Length == 1 && roots[0] == "*";
            if (!pathAllowed)
            {
                foreach (var root in roots)
                {
                    if (root == "*")
                    {
                        pathAllowed = true;
                        break;
                    }

                    var fullRoot = Path.GetFullPath(root);
                    if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                        (fullPath.Length == fullRoot.Length ||
                         fullPath[fullRoot.Length] == Path.DirectorySeparatorChar ||
                         fullPath[fullRoot.Length] == Path.AltDirectorySeparatorChar))
                    {
                        pathAllowed = true;
                        break;
                    }
                }
            }

            if (!pathAllowed || !File.Exists(fullPath))
                return null;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var fileName = Path.GetFileName(fullPath);
            var mimeType = GuessMimeTypeFromExtension(Path.GetExtension(fullPath));
            return await mediaCache.SaveAsync(bytes.AsMemory(), mimeType, fileName, ct);
        }
        catch
        {
            return null;
        }
    }

    private static string GuessMimeTypeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".py" => "text/x-python",
            ".sh" => "text/x-sh",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

    // Builds a newline-prefixed block of [FILE_URL:] markers to append to response text.
    // Format: [FILE_URL:/media/{id}|{fileName}] so preprocessMediaMarkers can show the real name.
    private static string BuildFileUrlSuffix(List<StoredMediaAsset> assets)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in assets)
        {
            var nameSuffix = string.IsNullOrWhiteSpace(a.FileName) ? "" : $"|{a.FileName}";
            sb.Append($"\n[FILE_URL:/media/{a.Id}{nameSuffix}]");
        }
        return sb.ToString();
    }

    // Appends [FILE_URL:] markers to the last assistant ChatTurn in history.
    // ChatTurn is a record so we replace the entry in History.
    private static void InjectFileUrlMarkersIntoHistory(Session session, List<StoredMediaAsset> assets)
    {
        if (assets.Count == 0) return;
        for (var i = session.History.Count - 1; i >= 0; i--)
        {
            var turn = session.History[i];
            if (!string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                continue;
            // Skip the [tool_use] placeholder turn — target the text response turn.
            if (string.Equals(turn.Content, "[tool_use]", StringComparison.Ordinal))
                continue;

            session.History[i] = turn with { Content = turn.Content + BuildFileUrlSuffix(assets) };
            return;
        }
    }
}

