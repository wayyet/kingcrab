using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Manage scheduled cron jobs. List, inspect, trigger, create, and delete jobs.
/// </summary>
internal sealed class CronTool : IToolWithContext
{
    private readonly ICronJobSource _cronSource;
    private readonly MessagePipeline _pipeline;
    private readonly GatewayAutomationService _automations;

    public CronTool(ICronJobSource cronSource, MessagePipeline pipeline, GatewayAutomationService automations)
    {
        _cronSource = cronSource;
        _pipeline = pipeline;
        _automations = automations;
    }

    public string Name => "cron";
    public string Description => "Manage scheduled cron jobs. List configured jobs, get details, trigger immediate execution, create new jobs, update existing jobs, or delete existing jobs.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list","get","run","create","update","delete","history"],"description":"Action to perform"},
        "name":{"type":"string","description":"Job name or ID (required for get/run/update/delete/history; used as display name for create)"},
        "schedule":{"type":"string","description":"Cron expression or shorthand like @daily, @hourly (required for create unless run_at is set; optional patch for update)"},
        "prompt":{"type":"string","description":"Prompt text for the job (required for create; optional patch for update)"},
        "timezone":{"type":"string","description":"IANA timezone, e.g. America/New_York (create/update)"},
        "model_id":{"type":"string","description":"LLM model override for this job (create/update)"},
        "session_id":{"type":"string","description":"Override the isolated session ID for this cron job. Leave unset to use an auto-generated isolated session (recommended); do NOT pass the current user's session ID."},
        "channel_id":{"type":"string","description":"Delivery channel ID (create/update)"},
        "recipient_id":{"type":"string","description":"Delivery recipient ID (create/update)"},
        "run_on_startup":{"type":"boolean","description":"Run immediately on gateway startup (create/update)"},
        "run_at":{"type":"string","format":"date-time","description":"ISO 8601 UTC datetime for a one-shot job (create). If set, schedule is ignored."},
        "delete_after_run":{"type":"boolean","description":"Delete this job after it runs once (used with run_at for one-shot jobs)"}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: cron requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var action = GetString(root, "action") ?? "list";

        return action switch
        {
            "list" => ListJobs(),
            "get" => GetJob(root),
            "run" => await RunJobAsync(root, ct),
            "create" => await CreateJobAsync(root, context, ct),
            "update" => await UpdateJobAsync(root, ct),
            "delete" => await DeleteJobAsync(root, ct),
            "history" => await GetHistoryAsync(root, ct),
            _ => $"Error: Unknown action '{action}'. Use list, get, run, create, update, delete, or history."
        };
    }

    private string ListJobs()
    {
        var jobs = _cronSource.GetJobs();
        if (jobs.Count == 0)
            return "No cron jobs configured.";

        var sb = new StringBuilder();
        sb.AppendLine($"Cron jobs ({jobs.Count}):");
        foreach (var job in jobs)
        {
            var label = string.IsNullOrWhiteSpace(job.DisplayName) ? job.Name : $"{job.DisplayName} [{job.Name}]";
            var schedule = job.RunAt.HasValue ? $"one-shot at {job.RunAt.Value:u}" : job.CronExpression;
            sb.AppendLine($"  {label}  {schedule}");
            var promptPreview = job.Prompt.Length > 60 ? job.Prompt[..60] + "…" : job.Prompt;
            sb.AppendLine($"    Prompt: {promptPreview}");
            if (job.RunOnStartup)
                sb.AppendLine("    RunOnStartup: true");
            if (!string.IsNullOrWhiteSpace(job.ModelId))
                sb.AppendLine($"    Model: {job.ModelId}");
        }
        return sb.ToString().TrimEnd();
    }

    private string GetJob(JsonElement root)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for get action.";

        var jobs = _cronSource.GetJobs();
        var job = FindJob(jobs, name);
        if (job is null)
            return $"Job '{name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Job: {(string.IsNullOrWhiteSpace(job.DisplayName) ? job.Name : job.DisplayName)}");
        sb.AppendLine($"  Id/Key: {job.Name}");
        if (job.RunAt.HasValue)
            sb.AppendLine($"  RunAt: {job.RunAt.Value:u} (one-shot)");
        else
            sb.AppendLine($"  Schedule: {job.CronExpression}");
        sb.AppendLine($"  Prompt: {job.Prompt}");
        sb.AppendLine($"  RunOnStartup: {job.RunOnStartup}");
        if (job.SessionId is not null) sb.AppendLine($"  SessionId: {job.SessionId}");
        if (job.ChannelId is not null) sb.AppendLine($"  ChannelId: {job.ChannelId}");
        if (job.RecipientId is not null) sb.AppendLine($"  RecipientId: {job.RecipientId}");
        if (job.Timezone is not null) sb.AppendLine($"  Timezone: {job.Timezone}");
        if (job.ModelId is not null) sb.AppendLine($"  Model: {job.ModelId}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> RunJobAsync(JsonElement root, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for run action.";

        var jobs = _cronSource.GetJobs();
        var job = FindJob(jobs, name);
        if (job is null)
            return $"Job '{name}' not found.";

        var message = new InboundMessage
        {
            ChannelId = job.ChannelId ?? "cron",
            SenderId = "cron",
            SessionId = job.SessionId ?? $"cron:{job.Name}",
            CronJobName = job.Name,
            Text = job.Prompt,
            Subject = job.Subject,
            IsSystem = true,
            ModelOverride = string.IsNullOrWhiteSpace(job.ModelId) ? null : job.ModelId
        };

        await _pipeline.InboundWriter.WriteAsync(message, ct);
        var label = string.IsNullOrWhiteSpace(job.DisplayName) ? job.Name : job.DisplayName;
        return $"Job '{label}' triggered for immediate execution.";
    }

    private async Task<string> CreateJobAsync(JsonElement root, ToolExecutionContext context, CancellationToken ct)
    {
        var name = GetString(root, "name");
        var schedule = GetString(root, "schedule");
        var prompt = GetString(root, "prompt");
        var runAtStr = GetString(root, "run_at");

        if (string.IsNullOrWhiteSpace(name)) return "Error: 'name' is required for create.";
        if (string.IsNullOrWhiteSpace(prompt)) return "Error: 'prompt' is required for create.";

        DateTimeOffset? runAt = null;
        bool deleteAfterRun = root.TryGetProperty("delete_after_run", out var dar) && dar.ValueKind == JsonValueKind.True;

        if (!string.IsNullOrWhiteSpace(runAtStr))
        {
            if (!DateTimeOffset.TryParse(runAtStr, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                return $"Error: 'run_at' value '{runAtStr}' is not a valid ISO 8601 date-time.";
            runAt = parsed;
            deleteAfterRun = true; // one-shot always cleans up unless user explicitly said false
            if (root.TryGetProperty("delete_after_run", out var explicitDar) && explicitDar.ValueKind == JsonValueKind.False)
                deleteAfterRun = false;
        }
        else if (string.IsNullOrWhiteSpace(schedule))
        {
            return "Error: 'schedule' is required for create (unless 'run_at' is set).";
        }

        var explicitChannelId = GetString(root, "channel_id");
        var explicitRecipientId = GetString(root, "recipient_id");
        var resolvedChannelId = explicitChannelId ?? context.Session.ChannelId;

        // "_user_*" are webchat-internal placeholder IDs; they are invalid on every real channel
        // (Feishu, Discord, email, …). Discard them and fall back to the authoritative session sender.
        var isPlaceholderRecipient = !string.IsNullOrWhiteSpace(explicitRecipientId)
            && explicitRecipientId.StartsWith("_user_", StringComparison.OrdinalIgnoreCase);
        var safeExplicitRecipientId = isPlaceholderRecipient ? null : explicitRecipientId;

        // If the delivery channel differs from the current session's channel, the caller must
        // explicitly supply a real recipient_id — the current session's SenderId is meaningless on a
        // foreign channel (e.g. webchat "_user_1" sent to Feishu would be rejected).
        if (!string.IsNullOrWhiteSpace(explicitChannelId)
            && !string.Equals(explicitChannelId, context.Session.ChannelId, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(safeExplicitRecipientId))
        {
            return $"Error: 'recipient_id' is required when 'channel_id' ('{explicitChannelId}') differs from the current session channel ('{context.Session.ChannelId}'). " +
                   $"Please provide the target user's ID for the '{explicitChannelId}' channel (e.g. Feishu open_id, Discord user ID, email address).";
        }

        var definition = new AutomationDefinition
        {
            Id = "",
            Name = name,
            Enabled = true,
            Schedule = schedule ?? "@hourly",
            Timezone = GetString(root, "timezone"),
            Prompt = prompt,
            ModelId = GetString(root, "model_id"),
            RunOnStartup = root.TryGetProperty("run_on_startup", out var ros) && ros.ValueKind == JsonValueKind.True,
            SessionId = GetString(root, "session_id"),
            DeliveryChannelId = resolvedChannelId,
            DeliveryRecipientId = safeExplicitRecipientId ?? context.Session.SenderId,
            IsDraft = false,
            Source = "agent",
            RunAt = runAt,
            DeleteAfterRun = deleteAfterRun
        };

        var saved = await _automations.SaveAsync(definition, ct);
        var summary = runAt.HasValue
            ? $"Created one-shot cron job '{saved.Name}' (id: {saved.Id}) to run at {runAt.Value:u}."
            : $"Created cron job '{saved.Name}' (id: {saved.Id}, schedule: {saved.Schedule}).";
        return summary;
    }

    private async Task<string> UpdateJobAsync(JsonElement root, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for update action.";

        // Resolve by ID first, then by name
        var existing = await _automations.GetAsync(name, ct);
        if (existing is null)
        {
            var all = await _automations.ListAsync(ct);
            existing = all.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
            return $"Job '{name}' not found. Only dynamic (agent-created) jobs can be updated.";

        // Collect optional patch fields — null means "keep existing"
        var schedule = GetString(root, "schedule");
        var prompt = GetString(root, "prompt");
        var timezone = GetString(root, "timezone");
        var modelId = GetString(root, "model_id");
        var sessionId = GetString(root, "session_id");
        var channelId = GetString(root, "channel_id");
        var recipientId = GetString(root, "recipient_id");

        // Discard webchat placeholder IDs — invalid on real channels
        var isPlaceholderRecipient = !string.IsNullOrWhiteSpace(recipientId)
            && recipientId.StartsWith("_user_", StringComparison.OrdinalIgnoreCase);
        var safeRecipientId = isPlaceholderRecipient ? null : recipientId;

        bool? runOnStartup = null;
        if (root.TryGetProperty("run_on_startup", out var rosProp))
            runOnStartup = rosProp.ValueKind == JsonValueKind.True;

        var patched = new AutomationDefinition
        {
            Id = existing.Id,
            Name = existing.Name,
            Enabled = existing.Enabled,
            Schedule = schedule ?? existing.Schedule,
            Timezone = timezone ?? existing.Timezone,
            Prompt = prompt ?? existing.Prompt,
            ModelId = modelId ?? existing.ModelId,
            RunOnStartup = runOnStartup ?? existing.RunOnStartup,
            SessionId = sessionId ?? existing.SessionId,
            DeliveryChannelId = channelId ?? existing.DeliveryChannelId,
            DeliveryRecipientId = safeRecipientId ?? existing.DeliveryRecipientId,
            DeliverySubject = existing.DeliverySubject,
            Tags = existing.Tags,
            IsDraft = existing.IsDraft,
            Source = existing.Source,
            TemplateKey = existing.TemplateKey,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            RunAt = existing.RunAt,
            DeleteAfterRun = existing.DeleteAfterRun,
        };

        var saved = await _automations.SaveAsync(patched, ct);
        return $"Updated cron job '{saved.Name}' (id: {saved.Id}).";
    }

    private async Task<string> DeleteJobAsync(JsonElement root, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for delete action.";

        // Try matching by ID first, then by name in dynamic automations
        var existing = await _automations.GetAsync(name, ct);
        if (existing is null)
        {
            var jobs = await _automations.ListAsync(ct);
            existing = jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
            return $"Job '{name}' not found in dynamic automations. Static jobs in appsettings cannot be deleted.";

        await _automations.DeleteAsync(existing.Id, ct);
        return $"Deleted cron job '{existing.Name}' (id: {existing.Id}).";
    }

    private async Task<string> GetHistoryAsync(JsonElement root, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for history action.";

        // Resolve to an automation ID (may be passed as name or ID)
        var existing = await _automations.GetAsync(name, ct);
        if (existing is null)
        {
            var all = await _automations.ListAsync(ct);
            existing = all.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is null)
            return $"Job '{name}' not found in dynamic automations.";

        var state = await _automations.GetRunStateAsync(existing.Id, ct);
        if (state is null || state.RecentRuns.Count == 0)
            return $"No run history for '{existing.Name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Run history for '{existing.Name}' (last {state.RecentRuns.Count}):");
        foreach (var entry in state.RecentRuns)
        {
            sb.AppendLine($"  {entry.RanAtUtc:u}  [{entry.Outcome}]  in:{entry.InputTokens} out:{entry.OutputTokens}");
            if (!string.IsNullOrWhiteSpace(entry.MessagePreview))
                sb.AppendLine($"    {entry.MessagePreview}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Find a job by Name (id) or DisplayName, case-insensitive.</summary>
    private static CronJobConfig? FindJob(IReadOnlyList<CronJobConfig> jobs, string nameOrDisplayName)
        => jobs.FirstOrDefault(j =>
            string.Equals(j.Name, nameOrDisplayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(j.DisplayName, nameOrDisplayName, StringComparison.OrdinalIgnoreCase));
}
