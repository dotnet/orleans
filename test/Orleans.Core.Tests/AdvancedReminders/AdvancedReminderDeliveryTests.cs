#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
public class AdvancedReminderDeliveryTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ProcessDueReminderAsync_WhenGrainTypeIsUnavailableAndCleanupEnabled_DeletesReminderWithoutTableScan()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "unavailable-type");
        var reminderTable = new MutableReminderTable(entry);
        var grainFactory = Substitute.For<IGrainFactory>();
        var clusterState = CreateClusterState();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal([(entry.ETag, true)], reminderTable.RemoveAttempts);
        Assert.Empty(grainFactory.ReceivedCalls());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenGrainTypeIsAvailable_DeliversNormally()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "available-type");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "available-type-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var clusterState = CreateClusterState(entry.GrainId.Type);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.Equal(2, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenActiveSiloManifestIsMissing_DoesNotDeleteReminder()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "incomplete-manifest");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "incomplete-manifest-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var clusterState = CreateIncompleteClusterState();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.NotNull(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenJobRunsEarly_ReschedulesWithoutFiring()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "early-job"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(5),
            NextDueUtc = now.UtcDateTime.AddMinutes(5),
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "early-job-dispatcher"));
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

        Assert.Empty(remindable.ReceivedStatuses);
        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(entry.NextDueUtc, current.NextDueUtc);
        Assert.NotEqual(entry.ScheduleId, current.ScheduleId);
        Assert.NotEmpty(current.JobId);
        Assert.NotEmpty(current.JobShardId);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsMissing_Returns()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(Task.FromResult<ReminderEntry?>(null));

        var service = CreateService(reminderTable);

        await service.ProcessDueReminderCoreAsync(GrainId.Create("test", "missing"), "r", expectedScheduleId: null, CancellationToken.None);

        await reminderTable.Received(1).ReadRow(Arg.Any<GrainId>(), "r");
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenETagDoesNotMatch_ReturnsWithoutUpsert()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "etag"),
            ReminderName = "r",
            StartAt = DateTime.UtcNow.AddMinutes(-5),
            NextDueUtc = DateTime.UtcNow.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
            ETag = "current",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        var service = CreateService(reminderTable);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: "stale", CancellationToken.None);

        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
        await reminderTable.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("delay")]
    [InlineData("utc")]
    [InlineData("offset")]
    public async Task OneShotReminder_DoesNotRepeatAfterCompletion(string timeForm)
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var grainId = GrainId.Create("test", "one-shot-lifecycle");
        var reminderTable = new MutableReminderTable(current: null);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "one-shot-lifecycle-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(1) },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var dueAt = now.AddMinutes(5);
        var schedule = timeForm switch
        {
            "delay" => ReminderSchedule.OneShot(TimeSpan.FromMinutes(5)),
            "utc" => ReminderSchedule.OneShot(dueAt.UtcDateTime),
            "offset" => ReminderSchedule.OneShot(dueAt.ToOffset(TimeSpan.FromMinutes(345))),
            _ => throw new ArgumentOutOfRangeException(nameof(timeForm)),
        };

        await service.RegisterOrUpdateReminder(
            grainId,
            "one-shot",
            schedule,
            MissedReminderAction.FireImmediately);
        var registered = await reminderTable.ReadRow(grainId, "one-shot");

        Assert.NotNull(registered);
        Assert.Equal(TimeSpan.Zero, registered.Period);
        Assert.Equal(dueAt.UtcDateTime, registered.NextDueUtc);
        Assert.NotEmpty(registered.ScheduleId);
        Assert.NotEmpty(registered.JobId);
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        await service.ProcessDueReminderCoreAsync(
            grainId,
            "one-shot",
            registered.ScheduleId,
            CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(TimeSpan.Zero, remindable.ReceivedStatuses[0].Period);
        Assert.Null(await reminderTable.ReadRow(grainId, "one-shot"));
        Assert.Contains(reminderTable.RemoveAttempts, attempt => attempt.Removed);

        // Completing the intended occurrence must not create a recurring schedule.
        timeProvider.Advance(TimeSpan.FromDays(30));
        await service.ProcessDueReminderCoreAsync(
            grainId, "one-shot", registered.ScheduleId, CancellationToken.None);
        await service.ProcessDueReminderCoreAsync(
            grainId, "one-shot", registered.ScheduleId, CancellationToken.None,
            durableJobDequeueCount: 2);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Null(await reminderTable.ReadRow(grainId, "one-shot"));
        Assert.Single(reminderTable.RemoveAttempts);
        await jobManager.Received(1).ScheduleJobAsync(
            Arg.Any<ScheduleJobRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_UsesOriginalGrainIdWhenResolvingRemindable()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("custom-remindable-type", "grain-key"),
            ReminderName = "typed",
            StartAt = now.AddMinutes(-5),
            NextDueUtc = now.AddSeconds(-5),
            Period = TimeSpan.FromMinutes(1),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-typed",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-typed-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "durable-reminder-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        grainFactory.Received(1).GetGrain<AdvancedRemindable>(entry.GrainId);
        AssertReminderReceived(remindable, "typed", status => Assert.Equal(entry.Period, status.Period));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_ForIntervalReminder_FiresAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "interval-due"),
            ReminderName = "interval",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(2),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-interval",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-interval-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "interval", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(entry.Period, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:interval", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
    }
}
