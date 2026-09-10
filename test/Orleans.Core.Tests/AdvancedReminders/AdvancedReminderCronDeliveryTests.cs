#nullable enable
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
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
public class AdvancedReminderCronDeliveryTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task ProcessDueReminderAsync_ForCronReminder_FiresAndReschedulesWithZeroPeriod()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "cron-due"),
            ReminderName = "cron",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.Zero,
            CronExpression = "*/5 * * * *",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-cron",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-cron-2");

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

        AssertReminderReceived(remindable, "cron", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(TimeSpan.Zero, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == TimeSpan.Zero
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
    }

    [Fact]
    public async Task CronReminder_WithoutTimeZone_FiresInUtcAfterTimeProviderAdvancesAndReschedules()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 8, 59, 50, TimeSpan.Zero));
        var expectedFirstDueUtc = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        var expectedNextDueUtc = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        var grainId = GrainId.Create("test", "cron-utc-runtime");
        var reminderTable = new MutableReminderTable(current: null);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });

        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cron-utc-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;

        await service.RegisterOrUpdateReminder(
            grainId,
            "utc-daily",
            ReminderSchedule.Cron("0 9 * * *"),
            MissedReminderAction.Skip);

        var initialEntry = await reminderTable.ReadRow(grainId, "utc-daily");
        Assert.NotNull(initialEntry);
        Assert.Equal(expectedFirstDueUtc, initialEntry.NextDueUtc);
        Assert.Equal(string.Empty, initialEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedFirstDueUtc), Assert.Single(scheduledRequests).DueTime);

        await ExecuteScheduledReminderAfterAdvancingTimeAsync(service, timeProvider, scheduledRequests[0], remindable);

        var status = Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(expectedFirstDueUtc, status.FirstTickTime);
        Assert.Equal(expectedFirstDueUtc, status.CurrentTickTime);
        Assert.Equal(TimeSpan.Zero, status.Period);

        var updatedEntry = await reminderTable.ReadRow(grainId, "utc-daily");
        Assert.NotNull(updatedEntry);
        Assert.Equal(expectedFirstDueUtc, updatedEntry.LastFireUtc);
        Assert.Equal(expectedNextDueUtc, updatedEntry.NextDueUtc);
        Assert.Equal(string.Empty, updatedEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedNextDueUtc), Assert.Single(scheduledRequests.Skip(1)).DueTime);
    }

    [Fact]
    public async Task CronReminder_WithTimeZone_FiresAtLocalTimeAfterTimeProviderAdvancesAndReschedules()
    {
        var timeZone = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var timeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone) ?? timeZone.Id;
        var expectedFirstDueUtc = AdvancedReminderTimeZoneTestHelper.ToUtc(timeZone, 2026, 1, 15, 9, 0, 0);
        var expectedNextDueUtc = AdvancedReminderTimeZoneTestHelper.ToUtc(timeZone, 2026, 1, 16, 9, 0, 0);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(expectedFirstDueUtc.AddSeconds(-10)));
        var grainId = GrainId.Create("test", "cron-paris-runtime");
        var reminderTable = new MutableReminderTable(current: null);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });

        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cron-paris-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;

        await service.RegisterOrUpdateReminder(
            grainId,
            "paris-daily",
            ReminderSchedule.Cron("0 9 * * *", timeZone.Id),
            MissedReminderAction.Skip);

        var initialEntry = await reminderTable.ReadRow(grainId, "paris-daily");
        Assert.NotNull(initialEntry);
        Assert.Equal(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc), expectedFirstDueUtc);
        Assert.Equal(expectedFirstDueUtc, initialEntry.NextDueUtc);
        Assert.Equal(timeZoneId, initialEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedFirstDueUtc), Assert.Single(scheduledRequests).DueTime);

        await ExecuteScheduledReminderAfterAdvancingTimeAsync(service, timeProvider, scheduledRequests[0], remindable);

        var status = Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(expectedFirstDueUtc, status.FirstTickTime);
        Assert.Equal(expectedFirstDueUtc, status.CurrentTickTime);
        Assert.Equal(TimeSpan.Zero, status.Period);

        var updatedEntry = await reminderTable.ReadRow(grainId, "paris-daily");
        Assert.NotNull(updatedEntry);
        Assert.Equal(expectedFirstDueUtc, updatedEntry.LastFireUtc);
        Assert.Equal(expectedNextDueUtc, updatedEntry.NextDueUtc);
        Assert.Equal(timeZoneId, updatedEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedNextDueUtc), Assert.Single(scheduledRequests.Skip(1)).DueTime);
    }
}
