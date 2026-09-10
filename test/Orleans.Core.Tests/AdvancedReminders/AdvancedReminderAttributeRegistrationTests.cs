#nullable enable
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderAttributeRegistrationTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ReconcileAttributeReminder_WhenUnchangedAtReactivation_PreservesNextTickAndDurableJob()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero));
        var grainId = GrainId.Create("test", "attribute-reactivation");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "attribute-reactivation-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var schedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        var declarationId = AttributeReminderRegistration.GetDeclarationId(
            schedule,
            MissedReminderAction.Skip);

        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            schedule,
            MissedReminderAction.Skip,
            declarationId);

        var firstRegistration = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(firstRegistration);
        Assert.Equal(new DateTime(2026, 8, 16, 11, 30, 0, DateTimeKind.Utc), firstRegistration.NextDueUtc);
        Assert.Single(scheduledRequests);
        Assert.Equal(2, reminderTable.UpsertCount);

        timeProvider.Advance(TimeSpan.FromMinutes(20));
        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)),
            MissedReminderAction.Skip,
            declarationId);

        var afterReactivation = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(afterReactivation);
        Assert.Equal(firstRegistration.NextDueUtc, afterReactivation.NextDueUtc);
        Assert.Equal(firstRegistration.ScheduleId, afterReactivation.ScheduleId);
        Assert.Equal(firstRegistration.JobId, afterReactivation.JobId);
        Assert.Equal(firstRegistration.JobShardId, afterReactivation.JobShardId);
        Assert.Single(scheduledRequests);
        Assert.Equal(2, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>());

        var remindable = Substitute.For<AdvancedRemindable>();
        remindable.ReceiveReminder(Arg.Any<string>(), Arg.Any<AdvancedTickStatus>()).Returns(Task.CompletedTask);
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await service.ProcessDueReminderCoreAsync(
            grainId,
            "every-thirty-minutes",
            afterReactivation.ScheduleId,
            CancellationToken.None);

        var followingOccurrence = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(followingOccurrence);
        Assert.Equal(new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc), followingOccurrence.NextDueUtc);
        Assert.NotEqual(afterReactivation.ScheduleId, followingOccurrence.ScheduleId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)),
            MissedReminderAction.Skip,
            declarationId);

        var afterFollowingReactivation = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(afterFollowingReactivation);
        Assert.Equal(followingOccurrence.NextDueUtc, afterFollowingReactivation.NextDueUtc);
        Assert.Equal(followingOccurrence.ScheduleId, afterFollowingReactivation.ScheduleId);
        Assert.Equal(followingOccurrence.JobId, afterFollowingReactivation.JobId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ReconcileAttributeReminder_WhenDeclarationChanges_ReplacesScheduleAndDurableJob()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero));
        var grainId = GrainId.Create("test", "attribute-update");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "attribute-update-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });
        jobManager.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var originalSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            originalSchedule,
            MissedReminderAction.Skip,
            AttributeReminderRegistration.GetDeclarationId(
                originalSchedule,
                MissedReminderAction.Skip));
        var original = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(original);

        timeProvider.Advance(TimeSpan.FromMinutes(20));
        var changedSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(40), TimeSpan.FromHours(1));
        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            changedSchedule,
            MissedReminderAction.FireImmediately,
            AttributeReminderRegistration.GetDeclarationId(
                changedSchedule,
                MissedReminderAction.FireImmediately));

        var updated = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(updated);
        Assert.Equal(new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc), updated.NextDueUtc);
        Assert.Equal(TimeSpan.FromHours(1), updated.Period);
        Assert.Equal(MissedReminderAction.FireImmediately, updated.Action);
        Assert.NotEqual(original.ScheduleId, updated.ScheduleId);
        Assert.NotEqual(original.JobId, updated.JobId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);
        await jobManager.Received(1).CancelAsync(
            Arg.Is<DurableJob>(job => job.Id == original.JobId && job.ShardId == original.JobShardId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAttributeReminder_WhenObsoleteJobCancellationThrows_CommitsReplacement()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero));
        var grainId = GrainId.Create("test", "attribute-update-cancel-failure");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "attribute-update-cancel-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        jobManager.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("Cancellation failed.")));
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var originalSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            originalSchedule,
            MissedReminderAction.Skip,
            AttributeReminderRegistration.GetDeclarationId(
                originalSchedule,
                MissedReminderAction.Skip));
        var original = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(original);
        var changedSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(40), TimeSpan.FromHours(1));

        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            changedSchedule,
            MissedReminderAction.FireImmediately,
            AttributeReminderRegistration.GetDeclarationId(
                changedSchedule,
                MissedReminderAction.FireImmediately));

        var updated = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(updated);
        Assert.Equal(TimeSpan.FromHours(1), updated.Period);
        Assert.NotEqual(original.ScheduleId, updated.ScheduleId);
        Assert.NotEqual(original.JobId, updated.JobId);
    }
}
