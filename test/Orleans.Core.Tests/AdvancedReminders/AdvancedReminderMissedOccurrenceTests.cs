#nullable enable
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
public class AdvancedReminderMissedOccurrenceTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedSkipAndNoFutureSchedule_RemovesReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "remove"),
            ReminderName = "r",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.Skip,
            ETag = "etag",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) });

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedNotifyAndNoFutureSchedule_RemovesReminderWithoutCallingGrain()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "notify-remove"),
            ReminderName = "notify",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.Notify,
            ETag = "etag-notify",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var remindable = Substitute.For<AdvancedRemindable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);

        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) },
            grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        Assert.Empty(remindable.ReceivedCalls());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedSkipAndFutureSchedule_DoesNotCallGrainAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "skip-reschedule"),
            ReminderName = "skip",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.Skip,
            ETag = "etag-skip",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-skip-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Empty(remindable.ReceivedCalls());
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc == null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:skip", Assert.NotNull(scheduledRequest).JobName);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedNotifyAndFutureSchedule_DoesNotCallGrainAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "notify-reschedule"),
            ReminderName = "notify",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.Notify,
            ETag = "etag-notify-future",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-notify-future-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Empty(remindable.ReceivedCalls());
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc == null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:notify", Assert.NotNull(scheduledRequest).JobName);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedFireImmediatelyAndNoFutureSchedule_FiresThenRemovesReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "fire-remove"),
            ReminderName = "fire",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-fire-remove",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var remindable = Substitute.For<AdvancedRemindable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "fire", status =>
        {
            Assert.Equal(TimeSpan.Zero, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedFireImmediatelyAndFutureSchedule_FiresAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "fire-reschedule"),
            ReminderName = "fire-future",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-fire-future",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-fire-future-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) },
            jobManager: jobManager,
            grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "fire-future", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(entry.Period, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc != null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await reminderTable.DidNotReceive().RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:fire-future", Assert.NotNull(scheduledRequest).JobName);
    }
}
