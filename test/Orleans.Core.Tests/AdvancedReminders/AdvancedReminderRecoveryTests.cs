#nullable enable
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
public class AdvancedReminderRecoveryTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task SchedulingFailure_LeavesPendingRowWhichReconciliationRepairsIdempotently()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "schedule-failure-recovery");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "schedule-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                if (scheduledRequests.Count == 1)
                {
                    throw new InvalidOperationException("injected scheduling failure");
                }

                return Task.FromResult(CreateDurableJob(request));
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recoverable",
            StartAt = now.AddMinutes(5),
            NextDueUtc = now.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None));

        var pending = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.False(string.IsNullOrWhiteSpace(pending.ScheduleId));
        Assert.Empty(pending.JobId);
        Assert.Empty(pending.JobShardId);

        await service.EnsureScheduledCoreAsync(
            grainId,
            entry.ReminderName,
            pending.ScheduleId,
            force: false,
            CancellationToken.None);

        Assert.Equal(2, scheduledRequests.Count);
        Assert.NotEqual(scheduledRequests[0].Metadata!["schedule-id"], scheduledRequests[1].Metadata!["schedule-id"]);
        var repaired = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.False(string.IsNullOrWhiteSpace(repaired.JobId));
        Assert.False(string.IsNullOrWhiteSpace(repaired.JobShardId));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenNextJobSchedulingFails_ReconcilesCurrentSchedule()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "next-schedule-failure"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "next-schedule-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var scheduleAttempts = 0;
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (Interlocked.Increment(ref scheduleAttempts) == 1)
                {
                    throw new InvalidOperationException("injected next-job scheduling failure");
                }

                return Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>()));
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None));

        var pending = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.NotEqual(entry.ScheduleId, pending.ScheduleId);
        Assert.Empty(pending.JobId);
        Assert.Empty(pending.JobShardId);

        // ProcessDueReminderAsync schedules its repair without an expected id so that this
        // newly-persisted occurrence, rather than the completed one, is repaired.
        await service.EnsureScheduledCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            expectedScheduleId: null,
            force: false,
            CancellationToken.None);

        var repaired = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.Equal(2, scheduleAttempts);
        Assert.NotEqual(pending.ScheduleId, repaired.ScheduleId);
        Assert.NotEmpty(repaired.JobId);
        Assert.NotEmpty(repaired.JobShardId);
    }

    [Fact]
    public async Task HandlePersistenceFailure_ReconciliationInvalidatesOrphanedDurableJob()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var grainId = GrainId.Create("test", "handle-persistence-recovery");
        var reminderTable = new MutableReminderTable(current: null) { FailUpsertCall = 2 };
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "handle-persistence-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var scheduledJobs = new List<DurableJob>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                var job = CreateDurableJob(request);
                scheduledJobs.Add(job);
                return Task.FromResult(job);
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);
        dispatcher.Service = service;
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recover-handle",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None));
        var pending = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.Empty(pending.JobId);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await service.EnsureScheduledCoreAsync(
            grainId,
            entry.ReminderName,
            pending.ScheduleId,
            force: false,
            CancellationToken.None);

        Assert.Equal(2, scheduledRequests.Count);
        Assert.NotEqual(scheduledRequests[0].Metadata!["schedule-id"], scheduledRequests[1].Metadata!["schedule-id"]);
        Assert.Equal(new DateTimeOffset(entry.NextDueUtc!.Value, TimeSpan.Zero), scheduledRequests[0].DueTime);
        Assert.Equal(scheduledRequests[0].DueTime, scheduledRequests[1].DueTime);
        var repaired = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.Equal(scheduledJobs[1].Id, repaired.JobId);
        Assert.Equal(scheduledJobs[1].ShardId, repaired.JobShardId);
        Assert.NotEqual(scheduledJobs[0].Id, repaired.JobId);

        var upsertsBeforeOrphanRuns = reminderTable.UpsertCount;
        await service.ProcessDueReminderCoreAsync(
            grainId,
            entry.ReminderName,
            scheduledRequests[0].Metadata!["schedule-id"],
            CancellationToken.None);

        Assert.Equal(upsertsBeforeOrphanRuns, reminderTable.UpsertCount);
        Assert.Equal(2, scheduledRequests.Count);
    }
}
