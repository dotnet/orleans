#nullable enable
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderRetainedDeliveryTests : AdvancedReminderServiceTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OneShot_WhenSuccessfulOrRetentionDisabled_RemovesDefinition(bool retain, bool fail)
    {
        var scenario = new Scenario(options: new()
        {
            DeliveryRetryPolicy = retain ? ReminderRetryPolicy.UntilSuccess : ReminderRetryPolicy.NoRetries,
        });
        scenario.Callback = () => fail ? Task.FromException(new InvalidOperationException("callback failed")) : Task.CompletedTask;

        await scenario.DeliverAsync();

        Assert.Single(scenario.Recipient.ReceivedStatuses);
        Assert.Null(await scenario.ReadAsync());
        Assert.Empty(scenario.Requests);
    }

    [Fact]
    public async Task TargetResolutionFailure_RetainsDeliveryBeforeCallbackCanRun()
    {
        var scenario = new Scenario();
        scenario.Factory.Configure().GetGrain<AdvancedRemindable>(scenario.Entry.GrainId)
            .Returns<AdvancedRemindable>(_ => throw new InvalidOperationException("target cannot be resolved"));

        await scenario.DeliverAsync();

        Assert.Empty(scenario.Recipient.ReceivedStatuses);
        Assert.NotEqual(scenario.Entry.ScheduleId, (await scenario.ReadAsync())!.ScheduleId);
        Assert.Single(scenario.Requests);
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("cron")]
    public async Task RetentionDisabled_FailedRecurringDelivery_AdvancesToNextSlot(string kind)
    {
        var scenario = new Scenario(kind, options: new() { DeliveryRetryPolicy = ReminderRetryPolicy.NoRetries });

        await scenario.DeliverAsync();

        Assert.Single(scenario.Recipient.ReceivedStatuses);
        Assert.Equal(Scenario.Now.UtcDateTime.AddMinutes(5), (await scenario.ReadAsync())!.NextDueUtc);
        Assert.Equal(Scenario.Now.AddMinutes(5), Assert.Single(scenario.Requests).DueTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringFailure_NoRetriesAdvances_ButLimitedOneRemovesRegistration(bool limited)
    {
        var scenario = new Scenario("interval", options: new()
        {
            DeliveryRetryPolicy = limited ? ReminderRetryPolicy.Limited(1) : ReminderRetryPolicy.NoRetries,
        });

        await scenario.DeliverAsync();

        Assert.Single(scenario.Recipient.ReceivedStatuses);
        if (limited)
        {
            Assert.Null(await scenario.ReadAsync());
            Assert.Empty(scenario.Requests);
        }
        else
        {
            Assert.Equal(Scenario.Now.UtcDateTime.AddMinutes(5), (await scenario.ReadAsync())!.NextDueUtc);
            Assert.Single(scenario.Requests);
        }
    }

    [Theory]
    [InlineData("one-shot", MissedReminderAction.Skip)]
    [InlineData("one-shot", MissedReminderAction.Notify)]
    [InlineData("interval", MissedReminderAction.Skip)]
    [InlineData("interval", MissedReminderAction.Notify)]
    [InlineData("cron", MissedReminderAction.Skip)]
    [InlineData("cron", MissedReminderAction.Notify)]
    public async Task RetentionEnabled_NeverAttemptedOverdueOccurrence_AppliesMissedPolicy(string kind, MissedReminderAction action)
    {
        var scenario = new Scenario(kind);
        scenario.Entry.Action = action;
        scenario.Table.ReplaceCurrent(scenario.Entry);
        scenario.Clock.Advance(TimeSpan.FromDays(2));

        await scenario.DeliverAsync();

        Assert.Empty(scenario.Recipient.ReceivedStatuses);
        if (kind == "one-shot")
        {
            Assert.Null(await scenario.ReadAsync());
            Assert.Empty(scenario.Requests);
        }
        else
        {
            Assert.Equal(Scenario.Now.AddDays(2).AddMinutes(5), Assert.Single(scenario.Requests).DueTime);
        }
    }

    [Theory]
    [InlineData("one-shot")]
    [InlineData("interval")]
    [InlineData("cron")]
    public async Task FailedDelivery_UsesCompletionTimeForRetry_AndPreservesCalendarAnchor(string kind)
    {
        var scenario = new Scenario(kind);
        scenario.Callback = () =>
        {
            scenario.Clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromException(new InvalidOperationException("slow callback failed"));
        };

        await scenario.DeliverAsync();

        var retained = Assert.IsType<ReminderEntry>(await scenario.ReadAsync());
        var request = Assert.Single(scenario.Requests);
        Assert.Equal(scenario.Clock.GetUtcNow().AddMinutes(1), request.DueTime);
        Assert.Equal(scenario.Entry.StartAt, retained.StartAt);
        Assert.Equal(Scenario.Now.UtcDateTime, retained.LastFireUtc);
        Assert.Equal(kind == "one-shot" ? request.DueTime.UtcDateTime : scenario.Entry.NextDueUtc, retained.NextDueUtc);
        Assert.NotEqual(scenario.Entry.ScheduleId, retained.ScheduleId);
        Assert.Equal(retained.ScheduleId, request.Metadata!["schedule-id"]);
        Assert.NotEmpty(retained.JobId);
        Assert.NotEmpty(retained.JobShardId);

        // An old job cannot execute the freshly persisted occurrence.
        await scenario.DeliverAsync();
        Assert.Single(scenario.Recipient.ReceivedStatuses);
        Assert.Single(scenario.Requests);

        scenario.Callback = () => Task.CompletedTask;
        scenario.Clock.Advance(request.DueTime - scenario.Clock.GetUtcNow());
        await scenario.DeliverAsync(retained.ScheduleId);
        Assert.Equal(2, scenario.Recipient.ReceivedStatuses.Count);
        var following = await scenario.ReadAsync();
        if (kind == "one-shot")
        {
            Assert.Null(following);
        }
        else
        {
            Assert.NotNull(following);
            Assert.Equal(Scenario.Now.UtcDateTime.AddMinutes(5), following.NextDueUtc);
            Assert.Equal(scenario.Entry.StartAt, following.StartAt);
        }
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("etag")]
    [InlineData("schedule")]
    public async Task FailedDelivery_WhenDefinitionChanges_DoesNotResurrectOrOverwriteIt(string mutation)
    {
        var scenario = new Scenario();
        var replacement = Clone(scenario.Entry,
            etag: mutation == "etag" ? "replacement-etag" : scenario.Entry.ETag,
            scheduleId: mutation == "schedule" ? "replacement-schedule" : scenario.Entry.ScheduleId,
            nextDueUtc: Scenario.Now.UtcDateTime.AddDays(1));
        scenario.Callback = () =>
        {
            scenario.Table.ReplaceCurrent(mutation == "removed" ? null : replacement);
            return Task.FromException(new InvalidOperationException("callback failed after mutation"));
        };

        await scenario.DeliverAsync();

        var current = await scenario.ReadAsync();
        if (mutation == "removed")
        {
            Assert.Null(current);
        }
        else
        {
            Assert.NotNull(current);
            Assert.Equal(replacement.ETag, current.ETag);
            Assert.Equal(replacement.ScheduleId, current.ScheduleId);
            Assert.Equal(replacement.NextDueUtc, current.NextDueUtc);
            Assert.Null(current.LastFireUtc);
        }

        Assert.Equal(0, scenario.Table.UpsertCount);
        Assert.Empty(scenario.Table.RemoveAttempts);
        Assert.Empty(scenario.Requests);
    }

    [Theory]
    [InlineData("one-shot", false)]
    [InlineData("one-shot", true)]
    [InlineData("interval", false)]
    [InlineData("interval", true)]
    [InlineData("cron", false)]
    [InlineData("cron", true)]
    public async Task FailedRetryScheduling_RecoveryFencesOldJobs_AndPreservesRetryDelay(string kind, bool failHandleWrite)
    {
        var scenario = new Scenario(kind, failUpsertCall: failHandleWrite ? 2 : 0);
        if (!failHandleWrite)
        {
            scenario.Jobs.Configure().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<DurableJob>>(_ => throw new InvalidOperationException("injected scheduling failure"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.DeliverAsync());

        var pending = Assert.IsType<ReminderEntry>(await scenario.ReadAsync());
        Assert.NotEqual(scenario.Entry.ScheduleId, pending.ScheduleId);
        Assert.Empty(pending.JobId);
        Assert.Empty(pending.JobShardId);
        Assert.Equal(Scenario.Now.UtcDateTime, pending.LastFireUtc);

        scenario.ConfigureScheduling();
        await scenario.Service.EnsureScheduledCoreAsync(
            pending.GrainId, pending.ReminderName, pending.ScheduleId, force: false, CancellationToken.None);

        var repaired = Assert.IsType<ReminderEntry>(await scenario.ReadAsync());
        var repairRequest = scenario.Requests[^1];
        Assert.NotEqual(pending.ScheduleId, repaired.ScheduleId);
        Assert.Equal(Scenario.Now.AddMinutes(1), repairRequest.DueTime);
        Assert.NotEmpty(repaired.JobId);
        Assert.NotEmpty(repaired.JobShardId);
        await scenario.DeliverAsync();
        await scenario.DeliverAsync(pending.ScheduleId);
        Assert.Single(scenario.Recipient.ReceivedStatuses);

        scenario.Callback = () => Task.CompletedTask;
        scenario.Clock.Advance(repairRequest.DueTime - scenario.Clock.GetUtcNow());
        await scenario.DeliverAsync(repaired.ScheduleId);
        Assert.Equal(2, scenario.Recipient.ReceivedStatuses.Count);
    }

    [Fact]
    public async Task FailedRetryDefinitionWrite_LeavesOriginalOccurrenceAvailableForRetry()
    {
        var scenario = new Scenario(failUpsertCall: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.DeliverAsync());

        var unchanged = Assert.IsType<ReminderEntry>(await scenario.ReadAsync());
        Assert.Equal(scenario.Entry.ScheduleId, unchanged.ScheduleId);
        Assert.Equal(scenario.Entry.ETag, unchanged.ETag);
        Assert.Equal(scenario.Entry.JobId, unchanged.JobId);
        Assert.Null(unchanged.LastFireUtc);
        Assert.Empty(scenario.Requests);

        await scenario.DeliverAsync();
        Assert.Equal(2, scenario.Recipient.ReceivedStatuses.Count);
        Assert.NotEqual(unchanged.ScheduleId, (await scenario.ReadAsync())!.ScheduleId);
        Assert.Single(scenario.Requests);
    }

    [Theory]
    [InlineData("one-shot", MissedReminderAction.Skip)]
    [InlineData("one-shot", MissedReminderAction.Notify)]
    [InlineData("interval", MissedReminderAction.Skip)]
    [InlineData("interval", MissedReminderAction.Notify)]
    [InlineData("cron", MissedReminderAction.Skip)]
    [InlineData("cron", MissedReminderAction.Notify)]
    public async Task FailedOccurrence_WhenRetryBecomesOverdue_IsRetriedInsteadOfSkipped(string kind, MissedReminderAction action)
    {
        var scenario = new Scenario(kind);
        scenario.Entry.Action = action;
        scenario.Table.ReplaceCurrent(scenario.Entry);
        await scenario.DeliverAsync();
        var retained = Assert.IsType<ReminderEntry>(await scenario.ReadAsync());
        scenario.Clock.Advance(TimeSpan.FromDays(2));
        scenario.Callback = () => Task.CompletedTask;

        await scenario.DeliverAsync(retained.ScheduleId);

        Assert.Equal(2, scenario.Recipient.ReceivedStatuses.Count);
        if (kind == "one-shot")
        {
            Assert.Null(await scenario.ReadAsync());
        }
        else
        {
            Assert.Equal(Scenario.Now.UtcDateTime.AddDays(2).AddMinutes(5), (await scenario.ReadAsync())!.NextDueUtc);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, 1)]
    public async Task LimitedPolicy_RetriesSameOccurrenceUntilConfiguredLimit(int maximumDeliveryAttempts, int dequeueCount)
    {
        var scenario = new Scenario(options: new() { DeliveryRetryPolicy = ReminderRetryPolicy.Limited(maximumDeliveryAttempts) });
        if (dequeueCount < maximumDeliveryAttempts)
        {
            await Assert.ThrowsAsync<ReminderDeliveryException>(() => scenario.DeliverAsync(dequeueCount: dequeueCount));
            Assert.Equal(scenario.Entry.ScheduleId, (await scenario.ReadAsync())!.ScheduleId);
        }
        else
        {
            await scenario.DeliverAsync(dequeueCount: dequeueCount);
            Assert.Null(await scenario.ReadAsync());
        }

        Assert.Equal(0, scenario.Table.UpsertCount);
        Assert.Empty(scenario.Requests);
    }

    [Fact]
    public async Task CancelledCallback_DoesNotAdvanceOrReplaceOccurrence()
    {
        var scenario = new Scenario();
        using var cancellation = new CancellationTokenSource();
        scenario.Callback = () =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.ProcessDueReminderCoreAsync(
            scenario.Entry.GrainId, scenario.Entry.ReminderName, scenario.Entry.ScheduleId, cancellation.Token, 1));

        Assert.Equal(scenario.Entry.ScheduleId, (await scenario.ReadAsync())!.ScheduleId);
        Assert.Equal(0, scenario.Table.UpsertCount);
        Assert.Empty(scenario.Table.RemoveAttempts);
        Assert.Empty(scenario.Requests);
    }

    private sealed class Scenario
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        public FakeTimeProvider Clock { get; } = new(Now);
        public ReminderEntry Entry { get; }
        public MutableReminderTable Table { get; }
        public IGrainFactory Factory { get; } = Substitute.For<IGrainFactory>();
        public ILocalDurableJobManager Jobs { get; } = Substitute.For<ILocalDurableJobManager>();
        public List<ScheduleJobRequest> Requests { get; } = [];
        public CallbackRemindable Recipient { get; }
        public AdvancedReminderService Service { get; }
        public Func<Task> Callback { get; set; } = () => Task.FromException(new InvalidOperationException("callback failed"));

        public Scenario(string kind = "one-shot", AdvancedReminderOptions? options = null, int failUpsertCall = 0)
        {
            Entry = CreateDueEntry(Now, "retained-delivery");
            Entry.Period = kind == "interval" ? TimeSpan.FromMinutes(5) : TimeSpan.Zero;
            Entry.CronExpression = kind == "cron" ? "*/5 * * * *" : string.Empty;
            Entry.CronTimeZoneId = kind == "cron" ? "UTC" : string.Empty;
            Entry.Action = MissedReminderAction.FireImmediately;
            Entry.JobId = "original-job";
            Entry.JobShardId = "original-shard";
            Table = new(Entry) { FailUpsertCall = failUpsertCall };
            Recipient = new(() => Callback());
            Factory.GetGrain<AdvancedRemindable>(Entry.GrainId).Returns(Recipient);
            var dispatcher = CreateDispatcherGrain(GrainId.Create("advanced-reminder-dispatcher", Entry.GrainId.ToString()));
            Factory.GetGrain<IAdvancedReminderDispatcherGrain>(Entry.GrainId.ToString(), null)
                .Returns(dispatcher);
            ConfigureScheduling();
            Service = CreateService(Table, options ?? new() { DeliveryRetryPolicy = ReminderRetryPolicy.UntilSuccess }, Jobs, Factory, Clock);
        }

        public void ConfigureScheduling()
            => Jobs.Configure().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<ScheduleJobRequest>();
                    Requests.Add(request);
                    return Task.FromResult(CreateDurableJob(request));
                });

        public Task DeliverAsync(string? scheduleId = null, int dequeueCount = 1)
            => Service.ProcessDueReminderCoreAsync(Entry.GrainId, Entry.ReminderName,
                scheduleId ?? Entry.ScheduleId, CancellationToken.None, dequeueCount);

        public Task<ReminderEntry?> ReadAsync() => Table.ReadRow(Entry.GrainId, Entry.ReminderName);
    }
}
