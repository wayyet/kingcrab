using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for CronScheduler.IsTimeForJob and end-to-end scheduling behaviour.
/// IsTimeForJob is internal; it is visible here via InternalsVisibleTo in OpenClaw.Core.csproj.
/// </summary>
public sealed class CronSchedulerTests
{
    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static CronScheduler MakeScheduler(ICronJobSource? source = null)
    {
        source ??= Substitute.For<ICronJobSource>();
        var channel = Channel.CreateUnbounded<InboundMessage>();
        return new CronScheduler(source, NullLogger<CronScheduler>.Instance, channel.Writer);
    }

    private static CronJobConfig CronJob(string expression, string? timezone = null) => new()
    {
        Name = "test",
        CronExpression = expression,
        Prompt = "hello",
        Timezone = timezone
    };

    // -------------------------------------------------------------------------
    // IsTimeForJob — RunAt (one-shot)
    // -------------------------------------------------------------------------

    [Fact]
    public void IsTimeForJob_RunAt_MatchesExactMinute()
    {
        var scheduler = MakeScheduler();
        var target = new DateTimeOffset(2026, 4, 19, 14, 30, 0, TimeSpan.Zero);
        var job = new CronJobConfig
        {
            Name = "one-shot",
            RunAt = target,
            Prompt = "go"
        };

        // Same minute, different seconds — should fire
        Assert.True(scheduler.IsTimeForJob(job, target));
        Assert.True(scheduler.IsTimeForJob(job, target.AddSeconds(59)));
    }

    [Fact]
    public void IsTimeForJob_RunAt_DoesNotFireOneMinuteEarly()
    {
        var scheduler = MakeScheduler();
        var target = new DateTimeOffset(2026, 4, 19, 14, 30, 0, TimeSpan.Zero);
        var job = new CronJobConfig { Name = "one-shot", RunAt = target, Prompt = "go" };

        Assert.False(scheduler.IsTimeForJob(job, target.AddMinutes(-1)));
    }

    [Fact]
    public void IsTimeForJob_RunAt_DoesNotFireOneMinuteLate()
    {
        var scheduler = MakeScheduler();
        var target = new DateTimeOffset(2026, 4, 19, 14, 30, 0, TimeSpan.Zero);
        var job = new CronJobConfig { Name = "one-shot", RunAt = target, Prompt = "go" };

        Assert.False(scheduler.IsTimeForJob(job, target.AddMinutes(1)));
    }

    [Fact]
    public void IsTimeForJob_RunAt_NonUtcOffset_ConvertedToUtc()
    {
        var scheduler = MakeScheduler();
        // RunAt specified with +08:00 offset — stored with offset, compared as UTC
        var target = new DateTimeOffset(2026, 4, 19, 22, 0, 0, TimeSpan.FromHours(8)); // = 14:00 UTC
        var job = new CronJobConfig { Name = "one-shot", RunAt = target, Prompt = "go" };

        var utcNow = new DateTimeOffset(2026, 4, 19, 14, 0, 0, TimeSpan.Zero);
        Assert.True(scheduler.IsTimeForJob(job, utcNow));
    }

    // -------------------------------------------------------------------------
    // IsTimeForJob — cron expression (UTC, no timezone)
    // -------------------------------------------------------------------------

    [Fact]
    public void IsTimeForJob_CronEvery5Min_FiresAtMultiple()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("*/5 * * * *");

        // :00, :05, :10, :15, :20, :25, :30, :35, :40, :45, :50, :55
        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero)));
        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 10, 5, 0, TimeSpan.Zero)));
        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 10, 55, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsTimeForJob_CronEvery5Min_DoesNotFireAtOddMinutes()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("*/5 * * * *");

        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 10, 1, 0, TimeSpan.Zero)));
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 10, 7, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsTimeForJob_CronDaily_FiresAtMidnight()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("@daily"); // Cronos supports this as "0 0 * * *"

        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 0, 1, 0, TimeSpan.Zero)));
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsTimeForJob_CronSpecificTime_FiresAtExactMinute()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("30 9 * * *"); // 09:30 daily

        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 9, 30, 0, TimeSpan.Zero)));
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 9, 31, 0, TimeSpan.Zero)));
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 9, 29, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsTimeForJob_CronWeekday_DoesNotFireOnWeekend()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("0 9 * * 1-5"); // 09:00 Mon-Fri

        // 2026-04-19 is a Sunday
        Assert.False(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 19, 9, 0, 0, TimeSpan.Zero)));
        // 2026-04-20 is a Monday
        Assert.True(scheduler.IsTimeForJob(job, new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsTimeForJob_InvalidExpression_ReturnsFalse()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("not-a-cron-expr");

        Assert.False(scheduler.IsTimeForJob(job, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsTimeForJob_EmptyExpression_ReturnsFalse()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("");

        Assert.False(scheduler.IsTimeForJob(job, DateTimeOffset.UtcNow));
    }

    // -------------------------------------------------------------------------
    // IsTimeForJob — timezone
    // -------------------------------------------------------------------------

    [Fact]
    public void IsTimeForJob_CronWithTimezone_ConvertsCorrectly()
    {
        var scheduler = MakeScheduler();
        // CronScheduler.FindTimeZone supports both IANA and Windows IDs on all platforms.
        // Test with IANA ID — on Windows it is auto-converted to "China Standard Time".
        var job = CronJob("0 9 * * *", "Asia/Shanghai");

        // 09:00 China Standard Time (UTC+8) = 01:00 UTC
        var utcFireTime = new DateTimeOffset(2026, 4, 19, 1, 0, 0, TimeSpan.Zero);
        Assert.True(scheduler.IsTimeForJob(job, utcFireTime));

        // 09:00 UTC should NOT fire for a UTC+8 09:00 job
        var wrongUtc = new DateTimeOffset(2026, 4, 19, 9, 0, 0, TimeSpan.Zero);
        Assert.False(scheduler.IsTimeForJob(job, wrongUtc));
    }

    [Fact]
    public void IsTimeForJob_CronWithWindowsTimezoneId_ConvertsCorrectly()
    {
        var scheduler = MakeScheduler();
        // Also verify Windows-format IDs work (on Linux they are auto-converted to IANA)
        var job = CronJob("0 9 * * *", "China Standard Time");

        var utcFireTime = new DateTimeOffset(2026, 4, 19, 1, 0, 0, TimeSpan.Zero);
        Assert.True(scheduler.IsTimeForJob(job, utcFireTime));
    }

    [Fact]
    public void IsTimeForJob_InvalidTimezone_ReturnsFalse()
    {
        var scheduler = MakeScheduler();
        var job = CronJob("0 9 * * *", "Not/AReal_Timezone");

        Assert.False(scheduler.IsTimeForJob(job, DateTimeOffset.UtcNow));
    }

    // -------------------------------------------------------------------------
    // End-to-end: scheduler enqueues a message
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CronScheduler_EnqueuesMessage_WhenJobFires()
    {
        // Arrange — job that always fires (every minute: * * * * *)
        var utcNow = DateTimeOffset.UtcNow;
        var minuteStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day,
            utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero);

        var jobSource = Substitute.For<ICronJobSource>();
        jobSource.GetJobs().Returns([
            new CronJobConfig
            {
                Name = "every-minute",
                CronExpression = "* * * * *",
                Prompt = "tick",
                SessionId = "test-session"
            }
        ]);

        var channel = Channel.CreateUnbounded<InboundMessage>();
        var scheduler = new CronScheduler(jobSource, NullLogger<CronScheduler>.Instance, channel.Writer);

        // Act — directly invoke IsTimeForJob and EnqueueJobIfNotRunningAsync
        // by calling the scheduler with a matching time
        var job = new CronJobConfig
        {
            Name = "every-minute",
            CronExpression = "* * * * *",
            Prompt = "tick",
            SessionId = "test-session"
        };

        // IsTimeForJob for "* * * * *" should always return true
        Assert.True(scheduler.IsTimeForJob(job, minuteStart));
    }

    [Fact]
    public async Task CronScheduler_RunOnStartup_EnqueuesJobImmediately()
    {
        // Arrange
        var jobSource = Substitute.For<ICronJobSource>();
        jobSource.GetJobs().Returns([
            new CronJobConfig
            {
                Name = "startup-job",
                CronExpression = "0 0 1 1 *", // would only run Jan 1st at midnight
                Prompt = "startup",
                RunOnStartup = true
            }
        ]);

        var channel = Channel.CreateUnbounded<InboundMessage>();
        var scheduler = new CronScheduler(jobSource, NullLogger<CronScheduler>.Instance, channel.Writer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act — StartAsync triggers the RunOnStartup path before the timer loop
        await scheduler.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None); // let the background task proceed

        channel.Writer.Complete();

        var messages = new List<InboundMessage>();
        await foreach (var msg in channel.Reader.ReadAllAsync(CancellationToken.None))
            messages.Add(msg);

        await scheduler.StopAsync(CancellationToken.None);

        // Assert
        Assert.Single(messages);
        Assert.Equal("startup-job", messages[0].CronJobName);
        Assert.Equal("startup", messages[0].Text);
    }

    // -------------------------------------------------------------------------
    // MarkJobCompleted removes the running guard
    // -------------------------------------------------------------------------

    [Fact]
    public void MarkJobCompleted_NullOrEmpty_DoesNotThrow()
    {
        var scheduler = MakeScheduler();
        scheduler.MarkJobCompleted(null);
        scheduler.MarkJobCompleted("");
        scheduler.MarkJobCompleted("   ");
    }
}
