using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal enum RunNowResult { Queued, NotFound, AlreadyRunning }

internal sealed class GatewayAutomationService
{
    public const string HeartbeatAutomationId = "heartbeat.default";

    private readonly ConcurrentDictionary<string, byte> _runningAutomations = new(StringComparer.OrdinalIgnoreCase);
    private readonly GatewayConfig _config;
    private readonly IAutomationStore _store;
    private readonly HeartbeatService _heartbeat;
    private readonly ILogger<GatewayAutomationService>? _logger;
    private AutomationDefinition[] _cachedAutomations = [];

    public GatewayAutomationService(
        GatewayConfig config,
        IAutomationStore store,
        HeartbeatService heartbeat,
        ILogger<GatewayAutomationService>? logger = null)
    {
        _config = config;
        _store = store;
        _heartbeat = heartbeat;
        _logger = logger;
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> ListAsync(CancellationToken ct)
    {
        var items = new List<AutomationDefinition>();
        if (_config.Cron.Enabled)
            items.AddRange(_config.Cron.Jobs.Select(MapLegacyJob));

        var storedAutomations = await _store.ListAutomationsAsync(ct);
        _cachedAutomations = [.. storedAutomations];
        items.AddRange(storedAutomations);

        var heartbeat = MapHeartbeatConfig(_heartbeat.LoadConfig());
        items.Add(heartbeat);

        return items
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<AutomationDefinition?> GetAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
            return MapHeartbeatConfig(_heartbeat.LoadConfig());

        var legacy = _config.Cron.Jobs.FirstOrDefault(item => string.Equals(GetLegacyAutomationId(item), automationId, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null)
            return MapLegacyJob(legacy);

        return await _store.GetAutomationAsync(automationId, ct);
    }

    public async ValueTask<AutomationDefinition> SaveAsync(AutomationDefinition automation, CancellationToken ct)
    {
        var normalized = Normalize(automation);
        if (string.Equals(normalized.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            _heartbeat.SaveConfig(new HeartbeatConfigDto
            {
                Enabled = normalized.Enabled,
                CronExpression = normalized.Schedule,
                Timezone = normalized.Timezone,
                DeliveryChannelId = normalized.DeliveryChannelId,
                DeliveryRecipientId = normalized.DeliveryRecipientId,
                DeliverySubject = normalized.DeliverySubject,
                ModelId = normalized.ModelId,
                Tasks =
                [
                    new HeartbeatTaskDto
                    {
                        Id = "heartbeat-automation",
                        TemplateKey = "custom",
                        Title = string.IsNullOrWhiteSpace(normalized.Name) ? "Heartbeat" : normalized.Name,
                        Instruction = normalized.Prompt,
                        Enabled = true
                    }
                ]
            });

            return MapHeartbeatConfig(_heartbeat.LoadConfig());
        }

        await _store.SaveAutomationAsync(normalized, ct);
        await RefreshCacheAsync(ct);
        return normalized;
    }

    public async ValueTask DeleteAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            _heartbeat.SaveConfig(new HeartbeatConfigDto { Enabled = false, Tasks = [] });
            return;
        }

        await _store.DeleteAutomationAsync(automationId, ct);
        await RefreshCacheAsync(ct);
    }

    public async ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            var status = _heartbeat.LoadStatus();
            if (status is null)
                return null;

            return new AutomationRunState
            {
                AutomationId = HeartbeatAutomationId,
                Outcome = status.Outcome,
                LastRunAtUtc = status.LastRunAtUtc,
                LastDeliveredAtUtc = status.LastDeliveredAtUtc,
                DeliverySuppressed = status.DeliverySuppressed,
                InputTokens = status.InputTokens,
                OutputTokens = status.OutputTokens,
                SessionId = status.SessionId,
                MessagePreview = status.MessagePreview
            };
        }

        return await _store.GetRunStateAsync(automationId, ct);
    }

    public ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct)
        => _store.SaveRunStateAsync(runState, ct);

    public async ValueTask AppendRunHistoryAsync(string automationId, RunHistoryEntry entry, CancellationToken ct)
    {
        const int MaxHistory = 20;
        var existing = await _store.GetRunStateAsync(automationId, ct);
        var history = existing?.RecentRuns ?? [];
        var updated = new List<RunHistoryEntry>(history.Count + 1) { entry };
        foreach (var h in history.Take(MaxHistory - 1))
            updated.Add(h);

        var newState = new AutomationRunState
        {
            AutomationId = automationId,
            Outcome = entry.Outcome,
            LastRunAtUtc = entry.RanAtUtc,
            LastDeliveredAtUtc = existing?.LastDeliveredAtUtc,
            DeliverySuppressed = existing?.DeliverySuppressed ?? false,
            InputTokens = entry.InputTokens,
            OutputTokens = entry.OutputTokens,
            SessionId = existing?.SessionId,
            MessagePreview = entry.MessagePreview,
            RecentRuns = updated
        };
        await _store.SaveRunStateAsync(newState, ct);
    }

    public async ValueTask<RunNowResult> RunNowAsync(string automationId, MessagePipeline pipeline, CancellationToken ct)
    {
        var automation = await GetAsync(automationId, ct);
        if (automation is null)
            return RunNowResult.NotFound;

        if (!_runningAutomations.TryAdd(automation.Id, 0))
        {
            _logger?.LogWarning("Skipping automation '{AutomationId}' because a previous run is still active.", automationId);
            return RunNowResult.AlreadyRunning;
        }

        try
        {
            var sessionId = string.IsNullOrWhiteSpace(automation.SessionId)
                ? $"automation:{automation.Id}"
                : automation.SessionId;

            var inbound = new InboundMessage
            {
                IsSystem = true,
                SessionId = sessionId,
                CronJobName = automation.Id,
                ChannelId = automation.DeliveryChannelId,
                SenderId = automation.DeliveryRecipientId ?? sessionId,
                Subject = automation.DeliverySubject,
                Text = automation.Prompt,
                ModelOverride = string.IsNullOrWhiteSpace(automation.ModelId) ? null : automation.ModelId
            };

            if (!pipeline.InboundWriter.TryWrite(inbound))
                await pipeline.InboundWriter.WriteAsync(inbound, ct);

            return RunNowResult.Queued;
        }
        catch
        {
            _runningAutomations.TryRemove(automation.Id, out _);
            throw;
        }
    }

    public void MarkRunCompleted(string? automationId)
    {
        if (!string.IsNullOrWhiteSpace(automationId))
            _runningAutomations.TryRemove(automationId!, out _);
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> MigrateLegacyAsync(bool apply, CancellationToken ct)
    {
        var migrated = new List<AutomationDefinition>();
        if (_config.Cron.Enabled)
            migrated.AddRange(_config.Cron.Jobs.Select(MapLegacyJob));

        var heartbeat = _heartbeat.LoadConfig();
        if (heartbeat.Enabled)
        {
            var mappedHeartbeat = MapHeartbeatConfig(heartbeat);
            migrated.Add(new AutomationDefinition
            {
                Id = mappedHeartbeat.Id,
                Name = mappedHeartbeat.Name,
                Enabled = false,
                Schedule = mappedHeartbeat.Schedule,
                Timezone = mappedHeartbeat.Timezone,
                Prompt = mappedHeartbeat.Prompt,
                ModelId = mappedHeartbeat.ModelId,
                RunOnStartup = mappedHeartbeat.RunOnStartup,
                SessionId = mappedHeartbeat.SessionId,
                DeliveryChannelId = mappedHeartbeat.DeliveryChannelId,
                DeliveryRecipientId = mappedHeartbeat.DeliveryRecipientId,
                DeliverySubject = mappedHeartbeat.DeliverySubject,
                Tags = mappedHeartbeat.Tags,
                IsDraft = true,
                Source = "migrated-heartbeat",
                TemplateKey = mappedHeartbeat.TemplateKey,
                CreatedAtUtc = mappedHeartbeat.CreatedAtUtc,
                UpdatedAtUtc = mappedHeartbeat.UpdatedAtUtc
            });
        }

        if (apply)
        {
            foreach (var automation in migrated.Where(static item => !string.Equals(item.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)))
                await _store.SaveAutomationAsync(automation, ct);
            await RefreshCacheAsync(ct);
        }

        return migrated;
    }

    public async ValueTask RefreshCacheAsync(CancellationToken ct)
    {
        _cachedAutomations = [.. await _store.ListAutomationsAsync(ct)];
    }

    public IReadOnlyList<AutomationTemplate> GetTemplates()
        =>
        [
            new AutomationTemplate
            {
                Key = "heartbeat",
                Label = "Heartbeat",
                Description = "Managed heartbeat automation compatible with the existing heartbeat flow.",
                Available = true
            },
            new AutomationTemplate
            {
                Key = "custom",
                Label = "Custom",
                Description = "A direct scheduled prompt delivered through any supported channel.",
                Available = true
            }
        ];

    public AutomationPreview BuildPreview(AutomationDefinition automation)
    {
        var normalized = Normalize(automation);
        var issues = new List<AutomationValidationIssue>();
        if (string.IsNullOrWhiteSpace(normalized.Name))
            issues.Add(new AutomationValidationIssue { Code = "name_required", Message = "Automation name is required." });
        if (string.IsNullOrWhiteSpace(normalized.Prompt))
            issues.Add(new AutomationValidationIssue { Code = "prompt_required", Message = "Automation prompt is required." });
        // One-shot jobs (RunAt set) don't need a cron schedule — skip schedule validation for them.
        if (!normalized.RunAt.HasValue)
        {
            if (string.IsNullOrWhiteSpace(normalized.Schedule))
                issues.Add(new AutomationValidationIssue { Code = "schedule_required", Message = "Automation schedule is required." });
            else if (!CronScheduler.IsValidExpression(normalized.Schedule))
                issues.Add(new AutomationValidationIssue
                {
                    Code = "invalid_schedule",
                    Message = $"'{normalized.Schedule}' is not a valid cron expression. " +
                              "Use standard 5-field format (e.g. '*/5 * * * *' for every 5 minutes, '0 9 * * 1-5' for weekdays at 09:00) " +
                              "or an alias such as @hourly, @daily, @weekly, @monthly."
                });
        }

        return new AutomationPreview
        {
            Definition = normalized,
            Issues = issues,
            Templates = GetTemplates(),
            PromptPreview = normalized.Prompt,
            EstimatedRunsPerMonth = EstimateRunsPerMonth(normalized.Schedule)
        };
    }

    public IReadOnlyList<CronJobConfig> BuildCronJobs()
    {
        var jobs = new List<CronJobConfig>();
        if (_config.Cron.Enabled && _config.Cron.Jobs is { Count: > 0 })
            jobs.AddRange(_config.Cron.Jobs);

        var managedHeartbeatJob = _heartbeat.BuildManagedJob();
        if (managedHeartbeatJob is not null)
            jobs.Add(managedHeartbeatJob);

        foreach (var automation in _cachedAutomations)
        {
            if (!automation.Enabled || automation.IsDraft)
                continue;

            jobs.Add(new CronJobConfig
            {
                Name = automation.Id,
                DisplayName = automation.Name,
                CronExpression = automation.Schedule,
                Prompt = automation.Prompt,
                RunOnStartup = automation.RunOnStartup,
                SessionId = automation.SessionId,
                ChannelId = automation.DeliveryChannelId,
                RecipientId = automation.DeliveryRecipientId,
                Subject = automation.DeliverySubject,
                Timezone = automation.Timezone,
                ModelId = automation.ModelId,
                RunAt = automation.RunAt,
                DeleteAfterRun = automation.DeleteAfterRun
            });
        }

        return jobs;
    }

    /// <summary>
    /// Tries to coerce a schedule string to a valid 5-field Cronos expression.
    /// Handles the common LLM mistake of supplying a 6-field Quartz/Spring expression by
    /// stripping the leading seconds field.
    /// </summary>
    private static string NormalizeSchedule(string schedule)
    {
        var trimmed = schedule.Trim();
        if (CronScheduler.IsValidExpression(trimmed))
            return trimmed;

        // Many LLMs emit Quartz/Spring 6-field expressions (seconds first).
        // Try dropping the first token to get a standard 5-field expression.
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 6)
        {
            var fiveField = string.Join(' ', parts.Skip(1));
            if (CronScheduler.IsValidExpression(fiveField))
                return fiveField;
        }
        return trimmed; // return as-is; validation will catch it later
    }

    private static AutomationDefinition Normalize(AutomationDefinition automation)
    {
        var now = DateTimeOffset.UtcNow;
        var rawSchedule = string.IsNullOrWhiteSpace(automation.Schedule) ? "@hourly" : automation.Schedule.Trim();
        return new AutomationDefinition
        {
            Id = string.IsNullOrWhiteSpace(automation.Id) ? $"automation-{Guid.NewGuid():N}"[..20] : automation.Id.Trim(),
            Name = automation.Name.Trim(),
            Enabled = automation.Enabled,
            Schedule = NormalizeSchedule(rawSchedule),
            Timezone = string.IsNullOrWhiteSpace(automation.Timezone) ? null : automation.Timezone.Trim(),
            Prompt = automation.Prompt ?? "",
            ModelId = string.IsNullOrWhiteSpace(automation.ModelId) ? null : automation.ModelId.Trim(),
            RunOnStartup = automation.RunOnStartup,
            SessionId = string.IsNullOrWhiteSpace(automation.SessionId) ? null : automation.SessionId.Trim(),
            DeliveryChannelId = string.IsNullOrWhiteSpace(automation.DeliveryChannelId) ? "cron" : automation.DeliveryChannelId.Trim(),
            DeliveryRecipientId = string.IsNullOrWhiteSpace(automation.DeliveryRecipientId) ? null : automation.DeliveryRecipientId.Trim(),
            DeliverySubject = string.IsNullOrWhiteSpace(automation.DeliverySubject) ? null : automation.DeliverySubject.Trim(),
            Tags = automation.Tags.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            IsDraft = automation.IsDraft,
            Source = string.IsNullOrWhiteSpace(automation.Source) ? "managed" : automation.Source.Trim(),
            TemplateKey = string.IsNullOrWhiteSpace(automation.TemplateKey) ? null : automation.TemplateKey.Trim(),
            CreatedAtUtc = automation.CreatedAtUtc == default ? now : automation.CreatedAtUtc,
            UpdatedAtUtc = now,
            RunAt = automation.RunAt,
            DeleteAfterRun = automation.DeleteAfterRun
        };
    }

    private static AutomationDefinition MapLegacyJob(CronJobConfig job)
        => new()
        {
            Id = GetLegacyAutomationId(job),
            Name = string.IsNullOrWhiteSpace(job.Name) ? "Legacy Cron Job" : job.Name,
            Enabled = true,
            Schedule = string.IsNullOrWhiteSpace(job.CronExpression) ? "@hourly" : job.CronExpression,
            Timezone = string.IsNullOrWhiteSpace(job.Timezone) ? null : job.Timezone,
            Prompt = job.Prompt ?? "",
            RunOnStartup = job.RunOnStartup,
            SessionId = job.SessionId,
            DeliveryChannelId = string.IsNullOrWhiteSpace(job.ChannelId) ? "cron" : job.ChannelId!,
            DeliveryRecipientId = job.RecipientId,
            DeliverySubject = job.Subject,
            Tags = ["legacy"],
            Source = "legacy-cron",
            TemplateKey = "custom"
        };

    private AutomationDefinition MapHeartbeatConfig(HeartbeatConfigDto config)
        => new()
        {
            Id = HeartbeatAutomationId,
            Name = "Managed Heartbeat",
            Enabled = config.Enabled,
            Schedule = config.CronExpression,
            Timezone = config.Timezone,
            Prompt = _heartbeat.BuildManagedPrompt(config, _heartbeat.RenderMarkdown(config)),
            ModelId = config.ModelId,
            DeliveryChannelId = config.DeliveryChannelId,
            DeliveryRecipientId = config.DeliveryRecipientId,
            DeliverySubject = config.DeliverySubject,
            Tags = ["heartbeat"],
            Source = "heartbeat",
            TemplateKey = "heartbeat"
        };

    private static string GetLegacyAutomationId(CronJobConfig job)
    {
        if (!string.IsNullOrWhiteSpace(job.Name))
            return $"legacy:{job.Name.Trim()}";

        var seed = $"{job.CronExpression}|{job.Prompt}|{job.ChannelId}|{job.RecipientId}|{job.Subject}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        return $"legacy:{hash[..12].ToLowerInvariant()}";
    }

    private static int EstimateRunsPerMonth(string schedule)
    {
        if (string.Equals(schedule, "@hourly", StringComparison.OrdinalIgnoreCase))
            return 24 * 30;
        if (string.Equals(schedule, "@daily", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (string.Equals(schedule, "@weekly", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (string.Equals(schedule, "@monthly", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 30;
    }
}
