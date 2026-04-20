using System.Text;
using System.Text.RegularExpressions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class ControlEndpoints
{
    public static void MapOpenClawControlEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operations = runtime.Operations;

        app.MapPost("/pairing/approve", (HttpContext ctx, string channelId, string senderId, string code) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            if (runtime.PairingManager.TryApprove(channelId, senderId, code, out var error))
            {
                AppendAudit(ctx, operations, auth, "pairing_approve", $"{channelId}:{senderId}", "Approved pairing.", true);
                return Results.Json(
                    new PairingApproveResponse
                    {
                        Success = true,
                        Message = "Approved successfully."
                    },
                    CoreJsonContext.Default.PairingApproveResponse);
            }

            if (error.Contains("Too many invalid attempts", StringComparison.Ordinal))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = error },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = error },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapPost("/pairing/revoke", (HttpContext ctx, string channelId, string senderId) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            runtime.PairingManager.Revoke(channelId, senderId);
            AppendAudit(ctx, operations, auth, "pairing_revoke", $"{channelId}:{senderId}", "Revoked pairing.", true);
            return Results.Json(
                new PairingRevokeResponse { Success = true },
                CoreJsonContext.Default.PairingRevokeResponse);
        });

        app.MapGet("/pairing/list", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(runtime.PairingManager.GetApprovedList().ToList(), CoreJsonContext.Default.ListString);
        });

        app.MapGet("/allowlists/{channelId}", (HttpContext ctx, string channelId) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var cfg = EndpointHelpers.GetConfigAllowlist(startup.Config, channelId);
            var dyn = runtime.Allowlists.TryGetDynamic(channelId);
            var effective = runtime.Allowlists.GetEffective(channelId, cfg);
            return Results.Json(
                new AllowlistSnapshotResponse
                {
                    ChannelId = channelId,
                    Semantics = runtime.AllowlistSemantics.ToString().ToLowerInvariant(),
                    Config = cfg,
                    Dynamic = dyn,
                    Effective = effective
                },
                CoreJsonContext.Default.AllowlistSnapshotResponse);
        });

        app.MapPost("/allowlists/{channelId}/add_latest", (HttpContext ctx, string channelId) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var latest = runtime.RecentSenders.TryGetLatest(channelId);
            if (latest is null)
                return Results.Json(
                    new SenderMutationResponse { Success = false, Error = "No recent sender found for that channel." },
                    CoreJsonContext.Default.SenderMutationResponse,
                    statusCode: StatusCodes.Status404NotFound);

            runtime.Allowlists.AddAllowedFrom(channelId, latest.SenderId);
            AppendAudit(ctx, operations, auth, "allowlist_add_latest", channelId, $"Added latest sender '{latest.SenderId}' to allowlist for '{channelId}'.", true);
            return Results.Json(
                new SenderMutationResponse { Success = true, SenderId = latest.SenderId },
                CoreJsonContext.Default.SenderMutationResponse);
        });

        app.MapPost("/allowlists/{channelId}/tighten", (HttpContext ctx, string channelId) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var paired = runtime.PairingManager.GetApprovedList()
                .Select(s =>
                {
                    var idx = s.IndexOf(':', StringComparison.Ordinal);
                    if (idx <= 0 || idx + 1 >= s.Length) return (Channel: "", Sender: "");
                    return (Channel: s[..idx], Sender: s[(idx + 1)..]);
                })
                .Where(t => string.Equals(t.Channel, channelId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Sender))
                .Select(t => t.Sender)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (paired.Length == 0)
                return Results.Json(
                    new CountMutationResponse { Success = false, Error = "No paired senders found for that channel." },
                    CoreJsonContext.Default.CountMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            runtime.Allowlists.SetAllowedFrom(channelId, paired);
            AppendAudit(ctx, operations, auth, "allowlist_tighten", channelId, $"Tightened allowlist for '{channelId}' to {paired.Length} paired sender(s).", true);
            return Results.Json(
                new CountMutationResponse { Success = true, Count = paired.Length },
                CoreJsonContext.Default.CountMutationResponse);
        });

        app.MapPost("/admin/reload-skills", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var loadedSkillNames = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            AppendAudit(ctx, operations, auth, "skills_reload", "skills", $"Reloaded {loadedSkillNames.Count} skill(s).", true);
            return Results.Json(
                new SkillsReloadResponse
                {
                    Reloaded = loadedSkillNames.Count,
                    Skills = loadedSkillNames
                },
                CoreJsonContext.Default.SkillsReloadResponse);
        });

        app.MapGet("/admin/skills", (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            var loggerFactory = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("SkillLoader");
            var allSkills = SkillLoader.LoadAll(startup.Config.Skills, startup.WorkspacePath, logger);
            var dtos = allSkills.Select(s => new SkillInfoDto
            {
                Name = s.Name,
                Description = s.Description,
                Emoji = s.Metadata.Emoji,
                Source = s.Source.ToString().ToLowerInvariant(),
                IsUserInstalled = s.Source == SkillSource.Workspace,
            }).ToList();
            return Results.Json(new SkillsDetailResponse { Skills = dtos }, CoreJsonContext.Default.SkillsDetailResponse);
        });

        app.MapPost("/admin/skills", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            SkillInstallRequest? request;
            try { request = await ctx.Request.ReadFromJsonAsync(CoreJsonContext.Default.SkillInstallRequest, ctx.RequestAborted); }
            catch { request = null; }

            if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Content))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "name and content are required." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            if (!Regex.IsMatch(request.Name, @"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "name must be 1-64 alphanumeric/hyphen/underscore characters starting with alphanumeric." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var workspacePath = startup.WorkspacePath
                ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);

            var skillDir = Path.Combine(workspacePath, "skills", request.Name);
            Directory.CreateDirectory(skillDir);
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            await File.WriteAllTextAsync(skillFile, request.Content, ctx.RequestAborted);

            var reloadedNames = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            AppendAudit(ctx, operations, auth, "skill_install", request.Name, $"Installed skill '{request.Name}'. Total: {reloadedNames.Count}.", true);
            return Results.Json(
                new SkillMutationResponse { Success = true, TotalLoaded = reloadedNames.Count, LoadedNames = reloadedNames },
                CoreJsonContext.Default.SkillMutationResponse);
        });

        app.MapDelete("/admin/skills/{name}", async (HttpContext ctx, string name) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            name = name.Trim().Trim('"').Trim('\'');
            if (!Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid skill name." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            // Only allow deleting user-installed (Workspace source) skills
            var delLoggerFactory = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
            var delLogger = delLoggerFactory.CreateLogger("SkillLoader");
            var currentSkills = SkillLoader.LoadAll(startup.Config.Skills, startup.WorkspacePath, delLogger);
            var targetSkill = currentSkills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (targetSkill is not null && targetSkill.Source != SkillSource.Workspace)
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Skill '{name}' is a built-in skill and cannot be deleted." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(startup.WorkspacePath))
            {
                var resolvedWs = OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
                if (string.IsNullOrWhiteSpace(resolvedWs))
                    return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);
                var skillDirDel = Path.Combine(resolvedWs, "skills", name);
                if (!Directory.Exists(skillDirDel))
                    return Results.Json(new SkillMutationResponse { Success = false, Error = $"User-installed skill '{name}' not found in workspace." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status404NotFound);
                Directory.Delete(skillDirDel, recursive: true);
                var reloadedNamesR = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
                AppendAudit(ctx, operations, auth, "skill_remove", name, $"Removed skill '{name}'. Total: {reloadedNamesR.Count}.", true);
                return Results.Json(new SkillMutationResponse { Success = true, TotalLoaded = reloadedNamesR.Count, LoadedNames = reloadedNamesR }, CoreJsonContext.Default.SkillMutationResponse);
            }

            var skillDir = Path.Combine(startup.WorkspacePath, "skills", name);
            if (!Directory.Exists(skillDir))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"User-installed skill '{name}' not found in workspace." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status404NotFound);

            Directory.Delete(skillDir, recursive: true);
            var reloadedNames = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            AppendAudit(ctx, operations, auth, "skill_remove", name, $"Removed skill '{name}'. Total: {reloadedNames.Count}.", true);
            return Results.Json(
                new SkillMutationResponse { Success = true, TotalLoaded = reloadedNames.Count, LoadedNames = reloadedNames },
                CoreJsonContext.Default.SkillMutationResponse);
        });

        app.MapPost("/admin/skills/upload", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var resolvedWorkspacePath = startup.WorkspacePath
                ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(resolvedWorkspacePath))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);

            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.Json(new SkillMutationResponse { Success = false, Error = "No file uploaded. Send multipart/form-data with field 'file'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var upload = ctx.Request.Form.Files[0];

            const long MaxBytes = 10 * 1024 * 1024; // 10 MB
            if (upload.Length > MaxBytes)
                return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP file too large (max 10 MB)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            // ── Phase 1: parse SKILL.md from the ZIP to extract skill name ──────
            System.IO.Compression.ZipArchiveEntry? skillMdEntry;
            string skillMdContent;
            try
            {
                using var stream1 = upload.OpenReadStream();
                using var zip1 = new System.IO.Compression.ZipArchive(stream1, System.IO.Compression.ZipArchiveMode.Read);

                skillMdEntry = zip1.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                    (e.FullName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                     e.FullName.Contains('/')));

                if (skillMdEntry is null)
                    return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP must contain a SKILL.md file (at any directory level)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

                using var mdStream = skillMdEntry.Open();
                skillMdContent = await new StreamReader(mdStream).ReadToEndAsync(ctx.RequestAborted);
            }
            catch (InvalidDataException)
            {
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid or corrupted ZIP file." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            // Extract skill name from SKILL.md frontmatter (SkillLoader.ParseSkillContent is internal)
            string? skillName = null;
            {
                var inFm = false;
                foreach (var rawLine in skillMdContent.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line == "---") { if (!inFm) { inFm = true; continue; } else break; }
                    if (!inFm) continue;
                    var ci = line.IndexOf(':');
                    if (ci < 0) continue;
                    if (line[..ci].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                    { skillName = line[(ci + 1)..].Trim().Trim('"').Trim('\''); break; }
                }
            }

            if (string.IsNullOrWhiteSpace(skillName))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "SKILL.md is missing a valid 'name:' frontmatter field." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            if (!Regex.IsMatch(skillName, @"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Skill name '{skillName}' contains invalid characters." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            // ── Phase 2: ZIP slip validation ─────────────────────────────────────
            var skillDir = Path.GetFullPath(Path.Combine(resolvedWorkspacePath, "skills", skillName));
            var skillDirPrefix = skillDir + Path.DirectorySeparatorChar;

            // Determine the prefix inside the ZIP to strip (= parent directory of SKILL.md)
            var lastSlashIdx = skillMdEntry.FullName.LastIndexOf('/');
            var zipPrefix = lastSlashIdx >= 0 ? skillMdEntry.FullName[..(lastSlashIdx + 1)] : "";

            try
            {
                using var stream2 = upload.OpenReadStream();
                using var zip2 = new System.IO.Compression.ZipArchive(stream2, System.IO.Compression.ZipArchiveMode.Read);

                foreach (var entry in zip2.Entries)
                {
                    // Strip the top-level directory prefix if present
                    var rel = zipPrefix.Length > 0 && entry.FullName.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullName[zipPrefix.Length..]
                        : entry.FullName;

                    if (string.IsNullOrEmpty(rel)) continue;

                    var destFull = Path.GetFullPath(Path.Combine(skillDir, rel));
                    if (!destFull.StartsWith(skillDirPrefix, StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP contains a path traversal entry and was rejected." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
                }
            }
            catch (InvalidDataException)
            {
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid or corrupted ZIP file." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            // ── Phase 3: extract ──────────────────────────────────────────────────
            if (Directory.Exists(skillDir))
                Directory.Delete(skillDir, recursive: true);
            Directory.CreateDirectory(skillDir);

            try
            {
                using var stream3 = upload.OpenReadStream();
                using var zip3 = new System.IO.Compression.ZipArchive(stream3, System.IO.Compression.ZipArchiveMode.Read);

                foreach (var entry in zip3.Entries)
                {
                    var rel = zipPrefix.Length > 0 && entry.FullName.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullName[zipPrefix.Length..]
                        : entry.FullName;

                    if (string.IsNullOrEmpty(rel) || rel.EndsWith('/') || rel.EndsWith('\\'))
                        continue; // skip directory entries

                    var destPath = Path.Combine(skillDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var entryStream = entry.Open();
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await entryStream.CopyToAsync(fs, ctx.RequestAborted);
                }
            }
            catch (Exception ex)
            {
                // Rollback on extraction failure
                if (Directory.Exists(skillDir))
                    Directory.Delete(skillDir, recursive: true);
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Extraction failed: {ex.Message}" }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }

            var reloadedSkills = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            AppendAudit(ctx, operations, auth, "skill_install_zip", skillName, $"Installed skill '{skillName}' via ZIP upload. Total: {reloadedSkills.Count}.", true);
            return Results.Json(
                new SkillMutationResponse { Success = true, TotalLoaded = reloadedSkills.Count, LoadedNames = reloadedSkills },
                CoreJsonContext.Default.SkillMutationResponse);
        });

        app.MapPost("/tools/approve", (HttpContext ctx, string approvalId, bool approved, string? requesterChannelId, string? requesterSenderId) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status429TooManyRequests);

            if (string.IsNullOrWhiteSpace(approvalId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "approvalId is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            if (!startup.Config.Security.RequireRequesterMatchForHttpToolApproval)
            {
                var adminOutcome = runtime.ToolApprovalService.TrySetDecisionWithRequest(approvalId, approved, requesterChannelId: null, requesterSenderId: null, requireRequesterMatch: false);
                if (adminOutcome.Result == ToolApprovalDecisionResult.Recorded && adminOutcome.Request is not null)
                {
                    runtime.RuntimeMetrics.IncrementApprovalDecisionsRecorded();
                    runtime.ApprovalAuditStore.RecordDecision(
                        adminOutcome.Request,
                        approved,
                        "http_admin",
                        auth.AuthMode == "browser-session" ? "browser" : "http",
                        auth.AuthMode);
                    AppendApprovalRuntimeEvent(
                        runtime,
                        adminOutcome.Request,
                        approved,
                        "http_admin",
                        auth.AuthMode == "browser-session" ? "browser" : "http",
                        auth.AuthMode);
                    AppendAudit(ctx, operations, auth, "tool_approval_admin", approvalId, $"Admin {(approved ? "approved" : "denied")} tool approval '{approvalId}'.", true);
                }
                else if (adminOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                {
                    runtime.RuntimeMetrics.IncrementApprovalDecisionsRejected();
                    AppendRejectedApprovalRuntimeEvent(runtime, adminOutcome.Request, approvalId, "requester_mismatch", "http", auth.AuthMode);
                }

                return adminOutcome.Result == ToolApprovalDecisionResult.Recorded
                    ? Results.Json(
                        new OperationStatusResponse
                        {
                            Success = true,
                            Mode = "admin_override"
                        },
                        CoreJsonContext.Default.OperationStatusResponse)
                    : Results.Json(
                        new OperationStatusResponse
                        {
                            Success = false,
                            Error = "No pending approval found for that id."
                        },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status404NotFound);
            }

            if (string.IsNullOrWhiteSpace(requesterChannelId) || string.IsNullOrWhiteSpace(requesterSenderId))
            {
                return Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "requesterChannelId and requesterSenderId are required when RequireRequesterMatchForHttpToolApproval=true."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var outcome = runtime.ToolApprovalService.TrySetDecisionWithRequest(
                approvalId,
                approved,
                requesterChannelId,
                requesterSenderId,
                requireRequesterMatch: true);

            if (outcome.Result == ToolApprovalDecisionResult.Recorded && outcome.Request is not null)
            {
                runtime.RuntimeMetrics.IncrementApprovalDecisionsRecorded();
                runtime.ApprovalAuditStore.RecordDecision(
                    outcome.Request,
                    approved,
                    "http_requester",
                    requesterChannelId,
                    requesterSenderId);
                AppendApprovalRuntimeEvent(
                    runtime,
                    outcome.Request,
                        approved,
                        "http_requester",
                        requesterChannelId,
                        requesterSenderId);
                AppendAudit(ctx, operations, auth, "tool_approval_admin_requester_match", approvalId, $"Admin {(approved ? "approved" : "denied")} tool approval '{approvalId}' with requester-match guard.", true);
            }
            else if (outcome.Result == ToolApprovalDecisionResult.Unauthorized)
            {
                runtime.RuntimeMetrics.IncrementApprovalDecisionsRejected();
                AppendRejectedApprovalRuntimeEvent(runtime, outcome.Request, approvalId, "requester_mismatch", requesterChannelId, requesterSenderId);
            }

            return outcome.Result switch
            {
                ToolApprovalDecisionResult.Recorded => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = true,
                        Mode = "admin_requester_match_guard"
                    },
                    CoreJsonContext.Default.OperationStatusResponse),
                ToolApprovalDecisionResult.Unauthorized => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "Requester does not match the pending approval owner for this admin approval request."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "No pending approval found for that id."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });
    }

    private static void AppendAudit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        EndpointHelpers.OperatorAuthorizationResult auth,
        string actionType,
        string targetId,
        string summary,
        bool success)
    {
        operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActionType = actionType,
            TargetId = targetId,
            Summary = summary,
            Success = success
        });
    }

    private static void AppendApprovalRuntimeEvent(
        GatewayAppRuntime runtime,
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

        runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
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

    private static void AppendRejectedApprovalRuntimeEvent(
        GatewayAppRuntime runtime,
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

        runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
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
}
