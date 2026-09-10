#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
public class AdvancedReminderRetryTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ProcessDueReminderAsync_WhenCallbackFails_ContinuesRecurringSeries()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "callback-failure"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "callback-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(now.UtcDateTime, current.LastFireUtc);
        Assert.Equal(now.UtcDateTime.AddMinutes(5), current.NextDueUtc);
        Assert.NotEmpty(current.JobId);
        Assert.NotEmpty(current.JobShardId);
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenOverdueRetryCallbackFailsBeforeMaximumDeliveryAttempts_RethrowsForDurableRetry()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "delivery-retry");
        entry.NextDueUtc = now.UtcDateTime.AddMinutes(-5);
        entry.Action = MissedReminderAction.Skip;
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MaximumDeliveryAttempts = 3 },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var context = CreateReminderJobContext(entry, dequeueCount: 2);

        var exception = await Assert.ThrowsAsync<ReminderDeliveryException>(
            () => dispatcher.ExecuteJobAsync(context, CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.NotNull(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.Equal(0, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenCallbackReachesMaximumDeliveryAttempts_DeletesReminder()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "delivery-delete");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MaximumDeliveryAttempts = 3 },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        await dispatcher.ExecuteJobAsync(CreateReminderJobContext(entry, dequeueCount: 3), CancellationToken.None);

        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal([(entry.ETag, true)], reminderTable.RemoveAttempts);
        Assert.Equal(0, reminderTable.UpsertCount);
    }
}
