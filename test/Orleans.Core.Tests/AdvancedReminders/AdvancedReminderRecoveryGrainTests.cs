#nullable enable
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderRecoveryGrainTests
{
    [Fact]
    public async Task InMemoryReminderTableGrain_RangePagingIsBounded()
    {
        var table = new AdvancedReminderTableGrain();
        for (var index = 0; index < 7; index++)
        {
            await table.UpsertRow(new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"paged-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow,
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });
        }

        var rows = new List<ReminderEntry>();
        string? continuationToken = null;
        do
        {
            var page = await table.ReadRows(0, 0, maxRows: 2, continuationToken);
            Assert.InRange(page.Reminders.Count, 0, 2);
            rows.AddRange(page.Reminders);
            continuationToken = page.ContinuationToken;
        } while (continuationToken is not null);

        Assert.Equal(7, rows.Count);
        Assert.Equal(7, rows.Select(row => (row.GrainId, row.ReminderName)).Distinct().Count());
    }

    [Fact]
    public async Task InMemoryReminderTableGrain_KeysetPagingDoesNotSkipAfterEarlierDeletion()
    {
        var table = new AdvancedReminderTableGrain();
        for (var index = 0; index < 5; index++)
        {
            await table.UpsertRow(new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"mutation-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow,
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });
        }

        var first = await table.ReadRows(0, 0, maxRows: 2, continuationToken: null);
        Assert.Equal(2, first.Reminders.Count);
        Assert.NotNull(first.ContinuationToken);
        var deleted = first.Reminders[0];
        Assert.True(await table.RemoveRow(deleted.GrainId, deleted.ReminderName, deleted.ETag));

        var rows = first.Reminders.ToList();
        var continuationToken = first.ContinuationToken;
        while (continuationToken is not null)
        {
            var page = await table.ReadRows(0, 0, maxRows: 2, continuationToken);
            rows.AddRange(page.Reminders);
            continuationToken = page.ContinuationToken;
        }

        Assert.Equal(5, rows.Select(row => (row.GrainId, row.ReminderName)).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_ScansInBoundedRangesAndDispatchesOnlyRequiredRows(bool force)
    {
        const int reminderCount = AdvancedReminderRecoveryGrain.RecoveryPageSize * 2 + 1;
        var entries = Enumerable.Range(0, reminderCount)
            .Select(index => new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"recovery-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow.AddMinutes(5),
                NextDueUtc = DateTime.UtcNow.AddMinutes(5),
                Period = TimeSpan.FromMinutes(1),
                ETag = $"etag-{index}",
                ScheduleId = $"schedule-{index}",
                JobId = index == 0 ? string.Empty : $"job-{index}",
                JobShardId = index == 0 ? string.Empty : $"shard-{index}",
            })
            .ToArray();
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        uint? pagedBegin = null;
        uint? pagedEnd = null;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(call =>
        {
            Interlocked.Increment(ref readCount);
            var begin = call.ArgAt<uint>(0);
            var end = call.ArgAt<uint>(1);
            var continuationToken = call.ArgAt<string?>(3);
            if (pagedBegin is null)
            {
                pagedBegin = begin;
                pagedEnd = end;
            }

            if (begin != pagedBegin || end != pagedEnd)
            {
                return new ReminderTableData();
            }

            var offset = continuationToken is null
                ? 0
                : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            var rows = entries.Skip(offset).Take(AdvancedReminderRecoveryGrain.RecoveryPageSize).ToArray();
            var nextOffset = offset + rows.Length;
            return new ReminderTableData(
                rows,
                nextOffset < entries.Length ? nextOffset.ToString(CultureInfo.InvariantCulture) : null);
        });
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance);

        await recovery.ReconcileAsync(force, CancellationToken.None);

        await reminderTable.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
        Assert.Equal(AdvancedReminderRecoveryGrain.ScanBucketsPerReconciliation + 2, readCount);
        await reminderTable.DidNotReceive().ReadRows((uint)0, (uint)0);
        await reminderTable.Received().ReadRows(
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            AdvancedReminderRecoveryGrain.RecoveryPageSize,
            Arg.Any<string?>());
        await dispatcher.Received(1).EnsureScheduledAsync(
            entries[0].GrainId,
            entries[0].ReminderName,
            entries[0].ScheduleId,
            force,
            Arg.Any<CancellationToken>());
        Assert.Equal(
            force ? reminderCount : 1,
            dispatcher.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAdvancedReminderDispatcherGrain.EnsureScheduledAsync)));
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotReplacePersistedJobHandles()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var overdue = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "overdue-recovery"),
            ReminderName = "overdue",
            StartAt = now.UtcDateTime.AddHours(-1),
            NextDueUtc = now.UtcDateTime.AddMinutes(-16),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "overdue-schedule",
            JobId = "overdue-job",
            JobShardId = "overdue-shard",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([overdue])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            timeProvider: timeProvider);

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        await dispatcher.DidNotReceive().EnsureScheduledAsync(
            overdue.GrainId,
            overdue.ReminderName,
            overdue.ScheduleId,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ExplicitRepairReplacesPersistedHandle()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "missing-job"),
            ReminderName = "missing-job",
            StartAt = DateTime.UtcNow.AddMinutes(-1),
            NextDueUtc = DateTime.UtcNow.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "missing-schedule",
            JobId = "missing-job-id",
            JobShardId = "missing-shard",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance);

        await recovery.ReconcileAsync(force: true, CancellationToken.None);

        await dispatcher.Received(1).EnsureScheduledAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            force: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_SchedulesFarFutureEntryWithMissingHandle()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "far-future"),
            ReminderName = "far-future",
            StartAt = now.UtcDateTime.AddDays(30),
            NextDueUtc = now.UtcDateTime.AddDays(30),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "far-future-schedule",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            new FakeTimeProvider(now));

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        await dispatcher.Received(1).EnsureScheduledAsync(
            entry.GrainId, entry.ReminderName, entry.ScheduleId, force: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_WhenOneDispatcherFails_ContinuesScanningOtherReminders()
    {
        var failedEntry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "failed-recovery"),
            ReminderName = "failed",
            StartAt = DateTime.UtcNow.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var successfulEntry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "successful-recovery"),
            ReminderName = "successful",
            StartAt = DateTime.UtcNow.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([failedEntry, successfulEntry])
                : new ReminderTableData());
        var failedDispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        failedDispatcher.EnsureScheduledAsync(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Injected reconciliation failure")));
        var successfulDispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(failedEntry.GrainId.ToString(), null).Returns(failedDispatcher);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(successfulEntry.GrainId.ToString(), null).Returns(successfulDispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance);

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        Assert.Equal(256, readCount);
        await successfulDispatcher.Received(1).EnsureScheduledAsync(
            successfulEntry.GrainId,
            successfulEntry.ReminderName,
            successfulEntry.ScheduleId,
            force: false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_WhenOneDispatcherHangs_TimesOutAndContinuesScanning()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "hanging-recovery"),
            ReminderName = "hanging",
            StartAt = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        dispatcher.EnsureScheduledAsync(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                dispatchStarted.TrySetResult();
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            });
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            timeProvider: timeProvider);

        var reconcileTask = recovery.ReconcileAsync(force: false, CancellationToken.None);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        timeProvider.Advance(AdvancedReminderRecoveryGrain.ReconciliationEntryTimeout);
        await reconcileTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(256, readCount);
    }

    [Fact]
    public async Task StartAsync_OnlyReconcilesWhenHeartbeatFindsScanDue()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(new ReminderTableData());
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            Substitute.For<IGrainFactory>(),
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            timeProvider: timeProvider);

        await recovery.StartAsync(force: false, CancellationToken.None);
        await recovery.StartAsync(force: false, CancellationToken.None);
        Assert.Equal(256, reminderTable.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows)));

        timeProvider.Advance(AdvancedReminderRecoveryGrain.ReconciliationPeriod);
        await recovery.StartAsync(force: false, CancellationToken.None);

        Assert.Equal(512, reminderTable.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows)));
        var rangeReads = reminderTable.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows))
            .ToArray();
        Assert.NotEqual(rangeReads[0].GetArguments()[0], rangeReads[AdvancedReminderRecoveryGrain.ScanBucketsPerReconciliation].GetArguments()[0]);
    }
}
