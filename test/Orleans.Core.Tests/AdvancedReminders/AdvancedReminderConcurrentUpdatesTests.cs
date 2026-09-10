#nullable enable
using NSubstitute;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderConcurrentUpdatesTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsRemovedDuringCallback_DoesNotResurrectReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "remove-during-fire"),
            ReminderName = "interval",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-remove-during-fire",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() =>
        {
            reminderTable.DeleteCurrent();
            return Task.CompletedTask;
        });
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal(0, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsUpdatedDuringCallback_DoesNotOverwriteNewSchedule()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "update-during-fire"),
            ReminderName = "cron",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.Zero,
            CronExpression = "*/5 * * * *",
            CronTimeZoneId = "UTC",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-before-update",
        };
        var updated = new ReminderEntry
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.Zero,
            CronExpression = "15 8 * * *",
            CronTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone()) ?? "America/New_York",
            Action = MissedReminderAction.Notify,
            ETag = "etag-after-update",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() =>
        {
            reminderTable.ReplaceCurrent(updated);
            return Task.CompletedTask;
        });
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(updated.ETag, current.ETag);
        Assert.Equal(updated.CronExpression, current.CronExpression);
        Assert.Equal(updated.CronTimeZoneId, current.CronTimeZoneId);
        Assert.Equal(updated.NextDueUtc, current.NextDueUtc);
        Assert.Equal(updated.Action, current.Action);
        Assert.Null(current.LastFireUtc);
        Assert.Equal(0, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderTimeZoneChangesBeforeOldJobFires_OldJobNoOps()
    {
        var current = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "tz-change"),
            ReminderName = "cron",
            StartAt = DateTime.UtcNow.AddHours(2),
            NextDueUtc = DateTime.UtcNow.AddHours(2),
            Period = TimeSpan.Zero,
            CronExpression = "0 9 * * *",
            CronTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone()) ?? "Asia/Kolkata",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-new-timezone",
        };
        var reminderTable = new MutableReminderTable(current);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(current.GrainId).Returns(remindable);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(current.GrainId, current.ReminderName, expectedScheduleId: "etag-old-timezone", CancellationToken.None);

        Assert.Empty(remindable.ReceivedStatuses);
        Assert.Equal(0, reminderTable.UpsertCount);
        Assert.Equal(current.ETag, (await reminderTable.ReadRow(current.GrainId, current.ReminderName))!.ETag);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }
}
