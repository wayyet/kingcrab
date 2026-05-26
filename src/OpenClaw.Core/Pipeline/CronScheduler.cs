using Cronos;
using TimeZoneConverter;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

/// <summary>
/// A simple background service that checks registered cron jobs every minute
/// and publishes an InboundMessage to the pipeline.
/// </summary>
public sealed class CronScheduler : BackgroundService
{
    private static readonly TimeSpan MaxRunningDuration = TimeSpan.FromHours(6);

    private readonly ICronJobSource _jobSource;
    private readonly ILogger<CronScheduler> _logger;
    private readonly ChannelWriter<InboundMessage> _pipelineChannel;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _runningJobs = new(StringComparer.OrdinalIgnoreCase);

    public CronScheduler(ICronJobSource jobSource, ILogger<CronScheduler> logger, ChannelWriter<InboundMessage> pipelineChannel)
    {
        _jobSource = jobSource;
        _logger = logger;
        _pipelineChannel = pipelineChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialJobs = _jobSource.GetJobs();
        if (initialJobs.Count == 0)
        {
            _logger.LogInformation("Cron Scheduler started with no jobs. Waiting for live cron registrations.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _logger.LogInformation("Cron Scheduler started. Monitoring {Count} initial jobs.", initialJobs.Count);

        // On startup: fire RunOnStartup jobs and catch up any missed one-shot (RunAt) jobs.
        // A one-shot job is considered "missed" if its RunAt time is in the past (within 1 hour)
        // and it has not yet been executed (DeleteAfterRun would have removed it if it had run).
        var startupNow = DateTimeOffset.UtcNow;
        var missedCutoff = startupNow - TimeSpan.FromHours(1);
        foreach (var job in initialJobs)
        {
            bool shouldRun = job.RunOnStartup;

            if (!shouldRun && job.RunAt.HasValue && !job.RunAt.Value.ToUniversalTime().Equals(default))
            {
                var targetUtc = job.RunAt.Value.ToUniversalTime();
                // Fire if the scheduled time already passed but is within the 1-hour catch-up window
                if (targetUtc <= startupNow && targetUtc >= missedCutoff)
                {
                    _logger.LogInformation(
                        "Cron job '{JobName}' was scheduled for {Target:u} (missed by {Elapsed:g}). Running now.",
                        job.Name, targetUtc, startupNow - targetUtc);
                    shouldRun = true;
                }
            }

            if (!shouldRun)
                continue;

            try
            {
                _logger.LogInformation("Triggering cron job '{JobName}' on startup at {Time}", job.Name, startupNow);
                await EnqueueJobIfNotRunningAsync(job, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to run cron job '{JobName}' on startup", job.Name);
            }
        }

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            CleanupStaleRunningJobs(DateTimeOffset.UtcNow);
            var jobs = _jobSource.GetJobs();
            if (jobs.Count == 0)
                continue;

            var utcNow = DateTimeOffset.UtcNow;

            // Re-evaluate jobs at the top of the minute
            foreach (var job in jobs)
            {
                if (IsTimeForJob(job, utcNow))
                {
                    _logger.LogInformation("Triggering cron job '{JobName}' at {Time}", job.Name, utcNow);
                    await EnqueueJobIfNotRunningAsync(job, stoppingToken);
                }
            }
        }
    }

    public void MarkJobCompleted(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            return;

        _runningJobs.TryRemove(jobName, out _);
    }

    private async ValueTask EnqueueJobIfNotRunningAsync(CronJobConfig job, CancellationToken ct)
    {
        var jobName = string.IsNullOrWhiteSpace(job.Name) ? "unnamed" : job.Name;
        var now = DateTimeOffset.UtcNow;
        if (_runningJobs.TryGetValue(jobName, out var runningSince))
        {
            if ((now - runningSince) <= MaxRunningDuration)
            {
                _logger.LogWarning("Skipping cron job '{JobName}' because a previous invocation is still running.", jobName);
                return;
            }

            _logger.LogWarning("Reaping stale running state for cron job '{JobName}' after {Duration}.", jobName, now - runningSince);
            _runningJobs.TryRemove(jobName, out _);
        }

        if (!_runningJobs.TryAdd(jobName, now))
        {
            _logger.LogWarning("Skipping cron job '{JobName}' because a previous invocation is still running.", jobName);
            return;
        }

        try
        {
            await EnqueueJobAsync(job, ct);
        }
        catch
        {
            _runningJobs.TryRemove(jobName, out _);
            throw;
        }
    }

    private async ValueTask EnqueueJobAsync(CronJobConfig job, CancellationToken ct)
    {
        var sessionId = job.SessionId ?? $"cron:{job.Name}";
        var channelId = job.ChannelId ?? "cron";

        // If a delivery RecipientId is explicitly set, send responses to that recipient.
        // Otherwise, set a stable "pseudo recipient" so the cron channel can bucket outputs per job/session.
        var senderId = job.RecipientId ?? sessionId ?? job.Name ?? "system";

        var msg = new InboundMessage
        {
            IsSystem = true,
            SessionId = sessionId,
            CronJobName = job.Name,
            ChannelId = channelId,
            SenderId = senderId,
            Subject = job.Subject ?? (string.IsNullOrWhiteSpace(job.Name) ? null : $"OpenClaw Cron: {job.Name}"),
            Text = job.Prompt,
            ModelOverride = string.IsNullOrWhiteSpace(job.ModelId) ? null : job.ModelId,
            DeleteAfterRun = job.DeleteAfterRun
        };

        await _pipelineChannel.WriteAsync(msg, ct);
    }

    /// <summary>
    /// Returns true if the job should fire now — either via RunAt (one-shot) or the cron expression.
    /// Timezone handling and DST-safe matching are delegated to Cronos.
    /// </summary>
    internal bool IsTimeForJob(CronJobConfig job, DateTimeOffset utcNow)
    {
        if (job.RunAt.HasValue)
        {
            // One-shot: fire if the current UTC minute matches the RunAt minute (window: 0–59 s into that minute)
            var target = job.RunAt.Value.ToUniversalTime();
            return utcNow.Year == target.Year
                && utcNow.Month == target.Month
                && utcNow.Day == target.Day
                && utcNow.Hour == target.Hour
                && utcNow.Minute == target.Minute;
        }

        if (string.IsNullOrWhiteSpace(job.CronExpression))
            return false;

        try
        {
            var tz = string.IsNullOrWhiteSpace(job.Timezone)
                ? TimeZoneInfo.Utc
                : FindTimeZone(job.Timezone);

            var expr = CronExpression.Parse(job.CronExpression, CronFormat.Standard);
            // Start of the current UTC minute
            var minuteStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day,
                utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero);
            // If the next occurrence after (minuteStart - 1s) is exactly minuteStart, fire now
            var next = expr.GetNextOccurrence(minuteStart.AddSeconds(-1), tz);
            return next.HasValue && next.Value.ToUniversalTime() == minuteStart;
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning("Cron job '{JobName}' skipped — invalid expression or timezone: {Error}", job.Name, ex.Message);
            return false;
        }
    }

    private void CleanupStaleRunningJobs(DateTimeOffset nowUtc)
    {
        foreach (var kvp in _runningJobs)
        {
            if ((nowUtc - kvp.Value) <= MaxRunningDuration)
                continue;

            if (_runningJobs.TryRemove(kvp.Key, out _))
            {
                _logger.LogWarning(
                    "Removed stale running marker for cron job '{JobName}' after {Duration}.",
                    kvp.Key,
                    nowUtc - kvp.Value);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="schedule"/> is a valid 5-field standard cron
    /// expression or a Cronos alias (@hourly, @daily, @weekly, @monthly, @yearly).
    /// </summary>
    public static bool IsValidExpression(string schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return false;
        try
        {
            CronExpression.Parse(schedule, CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether a cron expression fires at the given UTC time (ignoring seconds).
    /// Supports standard 5-field expressions plus Cronos aliases (@hourly, @daily, etc.)
    /// and special chars (L, W, #, ?).
    /// </summary>
    public static bool IsTime(string expression, DateTimeOffset utcTime)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        try
        {
            var expr = CronExpression.Parse(expression, CronFormat.Standard);
            var minuteStart = new DateTimeOffset(utcTime.Year, utcTime.Month, utcTime.Day,
                utcTime.Hour, utcTime.Minute, 0, TimeSpan.Zero);
            var next = expr.GetNextOccurrence(minuteStart.AddSeconds(-1), TimeZoneInfo.Utc);
            return next.HasValue && next.Value.ToUniversalTime() == minuteStart;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a timezone ID that may be either a Windows ID ("China Standard Time")
    /// or an IANA ID ("Asia/Shanghai"), on any platform and regardless of ICU availability.
    /// Uses the TimeZoneConverter package which embeds the full CLDR IANA↔Windows mapping table.
    /// </summary>
    private static TimeZoneInfo FindTimeZone(string timezoneId)
        => TZConvert.GetTimeZoneInfo(timezoneId);

}
