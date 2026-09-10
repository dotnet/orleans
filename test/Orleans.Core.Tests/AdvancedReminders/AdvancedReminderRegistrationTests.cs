#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Xunit;
using AdvancedReminderException = Orleans.AdvancedReminders.Runtime.ReminderException;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderRegistrationTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task RegisterOrUpdateReminder_FarFuture_SchedulesDurableJobImmediately()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "far-future-registration"),
            ReminderName = "far-future",
            StartAt = now.UtcDateTime.AddDays(30),
            NextDueUtc = now.UtcDateTime.AddDays(30),
            Period = TimeSpan.FromMinutes(5),
        };
        var reminderTable = new MutableReminderTable(null);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new DurableJob
            {
                Id = "far-future-job",
                ShardId = "far-future-shard",
                Name = call.Arg<ScheduleJobRequest>().JobName,
                DueTime = call.Arg<ScheduleJobRequest>().DueTime,
                TargetGrainId = call.Arg<ScheduleJobRequest>().Target,
            }));
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = new TestAdvancedReminderDispatcherGrain(GrainId.Create("sys", "far-future-dispatcher"));
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now));

        var handle = await service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Equal(2, reminderTable.UpsertCount);
        var persisted = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(persisted);
        Assert.Equal("far-future-job", persisted.JobId);
        Assert.Equal("far-future-shard", persisted.JobShardId);
        Assert.Equal(entry.StartAt, persisted.NextDueUtc);
        await jobManager.Received(1).ScheduleJobAsync(
            Arg.Is<ScheduleJobRequest>(request => request.DueTime == new DateTimeOffset(entry.StartAt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_PublicServiceEnforcesMinimumPeriodAndEnumValidation()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(2) },
            grainFactory: grainFactory);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterOrUpdateReminder(
            GrainId.Create("test", "public-validation"),
            "too-frequent",
            ReminderSchedule.Interval(TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            MissedReminderAction.Skip));

        _ = grainFactory.DidNotReceive().GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null);
        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_CancelsPreviousDurableJobAfterReplacementIsPersisted()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "cancel-old-job");
        var previous = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "replace",
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-old",
            ScheduleId = "schedule-old",
            JobId = "job-old",
            JobShardId = "shard-old",
        };
        var reminderTable = new MutableReminderTable(previous);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cancel-old-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        jobManager.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        await service.RegisterOrUpdateCoreAsync(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = previous.ReminderName,
            StartAt = now.AddHours(2),
            NextDueUtc = now.AddHours(2),
            Period = TimeSpan.FromMinutes(2),
        }, CancellationToken.None);

        await jobManager.Received(1).CancelAsync(
            Arg.Is<DurableJob>(job => job.Id == "job-old" && job.ShardId == "shard-old"),
            CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterReminder_CancelsPersistedDurableJob()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "cancel-unregistered-job");
        var current = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "remove",
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
            JobId = "job-current",
            JobShardId = "shard-current",
        };
        var reminderTable = new MutableReminderTable(current);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cancel-unregister-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.CancelAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.UnregisterCoreAsync(
            Assert.IsType<Orleans.AdvancedReminders.ReminderData>(current.ToIGrainReminder()),
            CancellationToken.None);

        await jobManager.Received(1).CancelAsync(
            Arg.Is<DurableJob>(job => job.Id == "job-current" && job.ShardId == "shard-current"),
            CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterReminder_AfterRecurringDelivery_UsesStableRegistrationId()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "unregister-after-delivery");
        var registered = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recurring",
            StartAt = now,
            NextDueUtc = now,
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-registered",
            ScheduleId = "r1:registration:occurrence-1",
        };
        var reminderTable = new MutableReminderTable(registered);
        var service = CreateService(reminderTable);
        var handle = Assert.IsType<Orleans.AdvancedReminders.ReminderData>(registered.ToIGrainReminder());

        reminderTable.ReplaceCurrent(Clone(
            registered,
            etag: "etag-after-delivery",
            scheduleId: "r1:registration:occurrence-2"));

        await service.UnregisterCoreAsync(handle, CancellationToken.None);

        Assert.Null(await reminderTable.ReadRow(grainId, registered.ReminderName));
        Assert.Equal([("etag-after-delivery", true)], reminderTable.RemoveAttempts);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WithCronSchedule_UpsertsAndSchedulesDurableJob()
    {
        var grainId = GrainId.Create("test", "cron-register");
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-1");

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null)
            .Returns(dispatcher);

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "cron",
            ReminderSchedule.Cron("0 9 * * *"),
            MissedReminderAction.Skip);

        Assert.Equal("cron", reminder.ReminderName);
        await reminderTable.Received().UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "cron"
            && entry.Period == TimeSpan.Zero
            && entry.CronExpression == "0 9 * * *"
            && entry.Action == MissedReminderAction.Skip
            && entry.NextDueUtc != null));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
        Assert.Equal(grainId.ToString(), request.Metadata!["grain-id"]);
        Assert.Equal("cron", request.Metadata["reminder-name"]);
        Assert.False(string.IsNullOrWhiteSpace(request.Metadata["schedule-id"]));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WithAbsoluteIntervalSchedule_UpsertsAndSchedulesDurableJob()
    {
        var grainId = GrainId.Create("test", "absolute-register");
        var dueAtUtc = DateTime.UtcNow.AddMinutes(10);
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-absolute");

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null)
            .Returns(dispatcher);

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "absolute",
            ReminderSchedule.Interval(dueAtUtc, TimeSpan.FromMinutes(5)),
            MissedReminderAction.Notify);

        Assert.Equal("absolute", reminder.ReminderName);
        await reminderTable.Received().UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "absolute"
            && entry.StartAt == dueAtUtc
            && entry.NextDueUtc == dueAtUtc
            && entry.Period == TimeSpan.FromMinutes(5)
            && entry.Action == MissedReminderAction.Notify
            && string.IsNullOrEmpty(entry.CronExpression)));
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:absolute", request.JobName);
        Assert.False(string.IsNullOrWhiteSpace(request.Metadata!["schedule-id"]));
    }

    [Fact]
    public async Task GetReminder_WhenEntryExists_ReturnsMappedHandle()
    {
        var grainId = GrainId.Create("test", "single");
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            ETag = "etag-1",
            CronExpression = "0 9 * * *",
            CronTimeZoneId = "UTC",
            Action = MissedReminderAction.Notify,
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult<ReminderEntry?>(entry));
        var service = CreateService(reminderTable);

        var result = await service.GetReminder(grainId, "r");

        var reminder = Assert.IsAssignableFrom<IGrainReminder>(result);
        Assert.Equal("r", reminder.ReminderName);
        Assert.Equal("0 9 * * *", reminder.CronExpression);
        Assert.Equal("UTC", reminder.CronTimeZone);
        Assert.Equal(MissedReminderAction.Notify, reminder.Action);
    }

    [Fact]
    public async Task GetReminders_WhenEntriesExist_ReturnsMappedHandles()
    {
        var grainId = GrainId.Create("test", "all");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRows(grainId).Returns(Task.FromResult(new ReminderTableData(
        [
            new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "interval",
                ETag = "etag-a",
                Action = MissedReminderAction.Skip,
            },
            new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "cron",
                ETag = "etag-b",
                CronExpression = "*/5 * * * *",
                CronTimeZoneId = "UTC",
                Action = MissedReminderAction.FireImmediately,
            },
        ])));
        var service = CreateService(reminderTable);

        var result = await service.GetReminders(grainId);
    }

    [Fact]
    public async Task UnregisterReminder_WithValidHandle_RemovesReminderUsingETag()
    {
        var grainId = GrainId.Create("test", "remove-valid");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult<ReminderEntry?>(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            ETag = "etag-remove",
            ScheduleId = "r1:registration:occurrence",
        }));
        reminderTable.RemoveRow(grainId, "r", "etag-remove").Returns(Task.FromResult(true));
        var service = CreateService(reminderTable);
        var reminder = await service.GetReminder(grainId, "r");

        await service.UnregisterCoreAsync(Assert.IsType<Orleans.AdvancedReminders.ReminderData>(reminder), CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(grainId, "r", "etag-remove");
    }

    [Fact]
    public async Task UnregisterReminder_WithForeignHandle_Throws()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var service = CreateService(reminderTable);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.UnregisterReminder(Substitute.For<IGrainReminder>()));

        Assert.Equal("reminder", exception.ParamName);
        await reminderTable.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task UnregisterReminder_WithStaleHandle_RejectsDeleteAndPreservesLatestReminder()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "stale-remove");
        var original = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            StartAt = now,
            NextDueUtc = now,
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-v1",
            ScheduleId = "r1:registration-v1:occurrence",
        };
        var reminderTable = new MutableReminderTable(original);
        var service = CreateService(reminderTable);
        var staleHandle = original.ToIGrainReminder();

        reminderTable.ReplaceCurrent(Clone(
            original,
            etag: "etag-v2",
            scheduleId: "r1:registration-v2:occurrence"));

        await Assert.ThrowsAsync<AdvancedReminderException>(
            () => service.UnregisterCoreAsync(
                Assert.IsType<Orleans.AdvancedReminders.ReminderData>(staleHandle),
                CancellationToken.None));

        var current = await reminderTable.ReadRow(grainId, "r");
        Assert.NotNull(current);
        Assert.Equal("etag-v2", current.ETag);
        Assert.Empty(reminderTable.RemoveAttempts);
    }
}
