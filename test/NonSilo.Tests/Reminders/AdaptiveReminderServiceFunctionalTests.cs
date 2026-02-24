#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class AdaptiveReminderServiceFunctionalTests
{
    [Fact]
    public async Task DoInitialPollAndQueue_RetriesOnceThenSucceeds()
    {
        var attempts = 0;
        var table = new InMemoryReminderTable
        {
            ReadRowsRangeOverride = (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.FromResult(new ReminderTableData());
            }
        };

        var service = CreateOperationalService(reminderTable: table);
        await InvokePrivateAsync(service, "DoInitialPollAndQueue");

        Assert.Equal(2, attempts);
        Assert.Equal((uint)2, GetFieldValue<uint>(service, "_initialReadCallCount"));
    }

    [Fact]
    public async Task DoInitialPollAndQueue_WhenRetryDelayIsCancelled_StopsLoop()
    {
        var service = CreateOperationalService();
        var cts = GetFieldValue<CancellationTokenSource>(service, "<StoppedCancellationTokenSource>k__BackingField");
        var attempts = 0;

        var table = new InMemoryReminderTable
        {
            ReadRowsRangeOverride = (_, _) =>
            {
                attempts++;
                cts.Cancel();
                throw new InvalidOperationException("transient");
            }
        };
        SetField(service, "_reminderTable", table);

        await InvokePrivateAsync(service, "DoInitialPollAndQueue");

        Assert.Equal(1, attempts);
        Assert.Equal((uint)1, GetFieldValue<uint>(service, "_initialReadCallCount"));
    }

    [Fact]
    public async Task RepairOverdueRows_RecomputesOnlyOverdueEntries()
    {
        var now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var overdue = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "overdue"),
            ReminderName = "overdue",
            StartAt = now.UtcDateTime.AddMinutes(-30),
            NextDueUtc = now.UtcDateTime.AddMinutes(-10),
            Period = TimeSpan.FromMinutes(2),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-overdue",
        };

        var fresh = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "fresh"),
            ReminderName = "fresh",
            StartAt = now.UtcDateTime.AddMinutes(-1),
            NextDueUtc = now.UtcDateTime.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-fresh",
        };

        var missingDue = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "missing"),
            ReminderName = "missing",
            StartAt = now.UtcDateTime.AddMinutes(-15),
            NextDueUtc = null,
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-missing",
        };

        var table = new InMemoryReminderTable();
        table.Seed(overdue);
        table.Seed(fresh);
        table.Seed(missingDue);

        var service = CreateOperationalService(
            reminderTable: table,
            options: new ReminderOptions { LookAheadWindow = TimeSpan.FromMinutes(2), PollInterval = TimeSpan.FromSeconds(1), BaseBucketSize = 8 },
            timeProvider: timeProvider);

        await InvokePrivateAsync(service, "RepairOverdueRows");

        var repaired = await table.ReadRow(overdue.GrainId, overdue.ReminderName);
        var unchangedFresh = await table.ReadRow(fresh.GrainId, fresh.ReminderName);
        var unchangedMissing = await table.ReadRow(missingDue.GrainId, missingDue.ReminderName);

        Assert.NotNull(repaired);
        Assert.NotNull(repaired.NextDueUtc);
        Assert.True(repaired.NextDueUtc > now.UtcDateTime);
        Assert.Equal(fresh.NextDueUtc, unchangedFresh.NextDueUtc);
        Assert.Null(unchangedMissing.NextDueUtc);
        Assert.Equal(1, table.UpsertCalls);
    }

    [Fact]
    public async Task RunPollLoopAsync_HandlesErrorsAndContinues()
    {
        var table = new InMemoryReminderTable
        {
            ReadRowsRangeOverride = (_, _) => throw new InvalidOperationException("poll failed")
        };

        var pollTimer = new SequenceTimer([true, false]);
        var service = CreateOperationalService(reminderTable: table, pollTimer: pollTimer);

        await InvokePrivateAsync(service, "RunPollLoopAsync");

        Assert.Equal(1, table.ReadRangeCalls);
    }

    [Fact]
    public async Task RunPollLoopAsync_StopsWhenCancelled()
    {
        var table = new InMemoryReminderTable();
        var pollTimer = new SequenceTimer([true, false]);
        var service = CreateOperationalService(reminderTable: table, pollTimer: pollTimer);
        var cts = GetFieldValue<CancellationTokenSource>(service, "<StoppedCancellationTokenSource>k__BackingField");
        cts.Cancel();

        await InvokePrivateAsync(service, "RunPollLoopAsync");

        Assert.Equal(0, table.ReadRangeCalls);
    }

    [Fact]
    public async Task RunRepairLoopAsync_HandlesErrorsAndContinues()
    {
        var table = new InMemoryReminderTable
        {
            ReadRowsRangeOverride = (_, _) => throw new InvalidOperationException("repair failed")
        };

        var repairTimer = new SequenceTimer([true, false]);
        var service = CreateOperationalService(reminderTable: table, repairTimer: repairTimer);

        await InvokePrivateAsync(service, "RunRepairLoopAsync");

        Assert.Equal(1, table.ReadRangeCalls);
    }

    [Fact]
    public async Task StartInBackground_SucceedsAndInitializesWorkers()
    {
        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(
            reminderTable: table,
            pollTimer: new SequenceTimer([false]),
            repairTimer: new SequenceTimer([false]));

        await InvokePrivateAsync(service, "StartInBackground");

        Assert.True(GetFieldValue<TaskCompletionSource<bool>>(service, "_startedTask").Task.IsCompletedSuccessfully);
        Assert.True(GetFieldValue<List<Task>>(service, "_workerTasks").Count > 0);
        Assert.Equal("Started", GetServiceStatusName(service));

        await service.Stop();
        Assert.Equal(1, table.StopCalls);
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenMissedAndSkip_AdvancesWithoutFiring()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(
            reminderTable: table,
            options: new ReminderOptions { PollInterval = TimeSpan.FromSeconds(5), LookAheadWindow = TimeSpan.FromMinutes(10), BaseBucketSize = 8 },
            timeProvider: timeProvider);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "skip"),
            ReminderName = "skip",
            StartAt = now.UtcDateTime.AddMinutes(-10),
            NextDueUtc = now.UtcDateTime.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-skip",
        };

        QueueForProcessing(service, entry, now.UtcDateTime.AddMinutes(1));
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 0, CancellationToken.None);

        var persisted = await table.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(persisted);
        Assert.Null(persisted.LastFireUtc);
        Assert.NotNull(persisted.NextDueUtc);
        Assert.True(persisted.NextDueUtc > now.UtcDateTime);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenMissedAndNotify_AdvancesWithoutFiring()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(
            reminderTable: table,
            options: new ReminderOptions { PollInterval = TimeSpan.FromSeconds(5), LookAheadWindow = TimeSpan.FromMinutes(10), BaseBucketSize = 8 },
            timeProvider: timeProvider);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "notify"),
            ReminderName = "notify",
            StartAt = now.UtcDateTime.AddMinutes(-10),
            NextDueUtc = now.UtcDateTime.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Notify,
            ETag = "etag-notify",
        };

        QueueForProcessing(service, entry, now.UtcDateTime.AddMinutes(1));
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 1, CancellationToken.None);

        var persisted = await table.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(persisted);
        Assert.Null(persisted.LastFireUtc);
        Assert.NotNull(persisted.NextDueUtc);
        Assert.True(persisted.NextDueUtc > now.UtcDateTime);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenNoFutureSchedule_DoesNotUpsert()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "none"),
            ReminderName = "none",
            StartAt = now.UtcDateTime.AddMinutes(-1),
            NextDueUtc = now.UtcDateTime.AddMinutes(-1),
            Period = TimeSpan.Zero,
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-none",
        };

        QueueForProcessing(service, entry, now.UtcDateTime.AddMinutes(1));
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 0, CancellationToken.None);

        Assert.Equal(0, table.UpsertCalls);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenOutOfRange_SkipsImmediately()
    {
        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, ringRange: new ConstantRingRange(false));

        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "outside"),
            ReminderName = "outside",
            StartAt = now.AddMinutes(-1),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-outside",
        };

        QueueForProcessing(service, entry, now.AddMinutes(1));
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 0, CancellationToken.None);

        Assert.Equal(0, table.UpsertCalls);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenCancelledDuringDelay_HandlesCancellation()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "cancel"),
            ReminderName = "cancel",
            StartAt = now.UtcDateTime.AddMinutes(5),
            NextDueUtc = now.UtcDateTime.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-cancel",
        };

        QueueForProcessing(service, entry, now.UtcDateTime.AddMinutes(10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 0, cts.Token);

        Assert.Equal(0, table.UpsertCalls);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task UnregisterReminder_WhenLatestRowMissing_Throws()
    {
        var table = new InMemoryReminderTable
        {
            RemoveRowOverride = (_, _, _) => Task.FromResult(false),
            ReadRowOverride = (_, _) => Task.FromResult<ReminderEntry?>(null)
        };
        var service = CreateOperationalService(reminderTable: table);

        var reminder = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "missing"),
            ReminderName = "missing",
            ETag = "stale",
        }.ToIGrainReminder();

        await Assert.ThrowsAsync<ReminderException>(() => service.UnregisterReminder(reminder));
    }

    [Fact]
    public async Task UnregisterReminder_WhenLatestRowExists_RemovesUsingLatestEtag()
    {
        var latest = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "latest"),
            ReminderName = "latest",
            ETag = "etag-latest",
        };

        var table = new InMemoryReminderTable
        {
            RemoveRowOverride = (_, _, etag) => Task.FromResult(etag == latest.ETag),
            ReadRowOverride = (_, _) => Task.FromResult<ReminderEntry?>(latest),
        };

        var service = CreateOperationalService(reminderTable: table);
        var reminder = new ReminderEntry
        {
            GrainId = latest.GrainId,
            ReminderName = latest.ReminderName,
            ETag = "etag-stale",
        }.ToIGrainReminder();

        await service.UnregisterReminder(reminder);
        Assert.Equal(2, table.RemoveCalls);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_Interval_StoresAndQueues()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "interval"),
            "interval",
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromMinutes(1),
            priority: ReminderPriority.High,
            action: MissedReminderAction.Notify);

        var row = await table.ReadRow(GrainId.Create("test", "interval"), "interval");
        Assert.NotNull(row);
        Assert.Equal(ReminderPriority.High, row.Priority);
        Assert.Equal(MissedReminderAction.Notify, row.Action);
        Assert.Equal("interval", reminder.ReminderName);
        Assert.Equal(ReminderPriority.High, reminder.Priority);
        Assert.Equal(1, table.UpsertCalls);
        Assert.True(GetEnqueuedCount(service) >= 1);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_AbsoluteUtc_StoresAndQueues()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);
        var dueAtUtc = now.UtcDateTime.AddSeconds(45);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "absolute"),
            "absolute",
            dueAtUtc,
            period: TimeSpan.FromMinutes(1),
            priority: ReminderPriority.High,
            action: MissedReminderAction.FireImmediately);

        var row = await table.ReadRow(GrainId.Create("test", "absolute"), "absolute");
        Assert.NotNull(row);
        Assert.Equal(dueAtUtc, row.StartAt);
        Assert.Equal(dueAtUtc, row.NextDueUtc);
        Assert.Equal(ReminderPriority.High, row.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, row.Action);
        Assert.Equal(ReminderPriority.High, reminder.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, reminder.Action);
        Assert.Equal(1, table.UpsertCalls);
        Assert.True(GetEnqueuedCount(service) >= 1);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_AbsoluteUtc_DefaultOverload_UsesNormalAndSkip()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);
        var dueAtUtc = now.UtcDateTime.AddSeconds(30);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "absolute-default"),
            "absolute-default",
            dueAtUtc,
            period: TimeSpan.FromMinutes(1));

        var row = await table.ReadRow(GrainId.Create("test", "absolute-default"), "absolute-default");
        Assert.NotNull(row);
        Assert.Equal(ReminderPriority.Normal, row.Priority);
        Assert.Equal(MissedReminderAction.Skip, row.Action);
        Assert.Equal(ReminderPriority.Normal, reminder.Priority);
        Assert.Equal(MissedReminderAction.Skip, reminder.Action);
    }

    [Fact]
    public async Task PollAndQueueDueReminders_QueuesReminderInsideLookAheadEvenIfNextPollIsLater()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var dueAtUtc = now.UtcDateTime.AddMinutes(4);
        var table = new InMemoryReminderTable();
        table.Seed(new ReminderEntry
        {
            GrainId = GrainId.Create("test", "bucketed"),
            ReminderName = "bucketed",
            StartAt = dueAtUtc,
            NextDueUtc = dueAtUtc,
            Period = TimeSpan.FromMinutes(30),
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-bucketed",
        });

        var service = CreateOperationalService(
            reminderTable: table,
            timeProvider: timeProvider,
            options: new ReminderOptions
            {
                PollInterval = TimeSpan.FromMinutes(9),
                LookAheadWindow = TimeSpan.FromMinutes(10),
                BaseBucketSize = 8,
                EnablePriority = true,
            });

        await InvokePrivateAsync(service, "PollAndQueueDueReminders");

        Assert.Equal(1, GetEnqueuedCount(service));
        var queue = GetFieldValue<Channel<ReminderEntry>>(service, "_deliveryQueue");
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.NotNull(queued);
        Assert.Equal("bucketed", queued.ReminderName);
        Assert.Equal(dueAtUtc, queued.NextDueUtc);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WhenBucketAlreadyPulled_NewReminderIsQueuedViaPublicApi()
    {
        var now = new DateTimeOffset(2026, 2, 1, 14, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var activationWorkingSet = Substitute.For<IActivationWorkingSet>();
        activationWorkingSet.Count.Returns(5_000_000); // Force adaptive bucket size down to 1.

        var table = new InMemoryReminderTable();
        table.Seed(new ReminderEntry
        {
            GrainId = GrainId.Create("test", "bucket-existing-1"),
            ReminderName = "bucket-existing-1",
            StartAt = now.UtcDateTime.AddMinutes(4),
            NextDueUtc = now.UtcDateTime.AddMinutes(4),
            Period = TimeSpan.FromMinutes(10),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-existing-1",
        });
        table.Seed(new ReminderEntry
        {
            GrainId = GrainId.Create("test", "bucket-existing-2"),
            ReminderName = "bucket-existing-2",
            StartAt = now.UtcDateTime.AddMinutes(4),
            NextDueUtc = now.UtcDateTime.AddMinutes(4),
            Period = TimeSpan.FromMinutes(10),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-existing-2",
        });

        var service = CreateOperationalService(
            reminderTable: table,
            activationWorkingSet: activationWorkingSet,
            timeProvider: timeProvider,
            options: new ReminderOptions
            {
                PollInterval = TimeSpan.FromMinutes(1),
                LookAheadWindow = TimeSpan.FromMinutes(5),
                BaseBucketSize = 1,
                EnablePriority = true,
            });
        activationWorkingSet.Count.Returns(5_000_000);

        // 14:00 poll pulls the first bucket.
        await InvokePrivateAsync(service, "PollAndQueueDueReminders");
        var queuedBeforeRegister = GetEnqueuedCount(service);
        Assert.True(queuedBeforeRegister > 0);

        // 14:01 a new reminder is registered via public API with due at 14:02.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "new-via-public-api"),
            "new-via-public-api",
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1),
            priority: ReminderPriority.Normal,
            action: MissedReminderAction.Skip);

        Assert.Equal(queuedBeforeRegister + 1, GetEnqueuedCount(service));
        var queue = GetFieldValue<Channel<ReminderEntry>>(service, "_deliveryQueue");
        var queuedNames = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Reader.TryRead(out var queued))
        {
            queuedNames.Add(queued.ReminderName);
        }

        Assert.Contains("new-via-public-api", queuedNames);
    }

    [Fact]
    public async Task ProcessReminderAsync_WhenUpsertThrows_DoesNotPropagateAndCleansQueueState()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable
        {
            UpsertRowOverride = _ => throw new InvalidOperationException("upsert-fail"),
        };

        var service = CreateOperationalService(
            reminderTable: table,
            timeProvider: timeProvider,
            options: new ReminderOptions
            {
                PollInterval = TimeSpan.FromSeconds(5),
                LookAheadWindow = TimeSpan.FromMinutes(10),
                BaseBucketSize = 8,
            });

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "upsert-fail"),
            ReminderName = "upsert-fail",
            StartAt = now.UtcDateTime.AddMinutes(-10),
            NextDueUtc = now.UtcDateTime.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-upsert-fail",
        };

        QueueForProcessing(service, entry, now.UtcDateTime.AddMinutes(1));
        await InvokePrivateAsync(service, "ProcessReminderAsync", entry, 0, CancellationToken.None);

        Assert.Equal(1, table.UpsertCalls);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_Cron_TrimsExpressionAndStores()
    {
        var now = new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);

        const string cron = "  */2 * * * * *  ";
        await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "cron"),
            "cron",
            cron,
            ReminderPriority.High,
            MissedReminderAction.Skip);

        var row = await table.ReadRow(GrainId.Create("test", "cron"), "cron");
        Assert.NotNull(row);
        Assert.Equal("*/2 * * * * *", row.CronExpression);
        Assert.Equal(TimeSpan.Zero, row.Period);
        Assert.Equal(ReminderPriority.High, row.Priority);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_Cron_UpdateFromUtcToUsEastern_RecomputesNextDueInLocalTime()
    {
        var now = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var usEastern = GetUsEasternTimeZone();

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);
        var grainId = GrainId.Create("test", "cron-utc-to-us");
        const string reminderName = "cron-utc-to-us";
        const string cron = "0 9 * * *";

        await service.RegisterOrUpdateReminder(grainId, reminderName, cron);
        var utcRow = await table.ReadRow(grainId, reminderName);
        Assert.NotNull(utcRow);
        Assert.Null(utcRow.CronTimeZoneId);
        Assert.Equal(new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc), utcRow.NextDueUtc);

        await service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cron,
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            usEastern.Id);

        var usRow = await table.ReadRow(grainId, reminderName);
        Assert.NotNull(usRow);
        Assert.False(string.IsNullOrWhiteSpace(usRow.CronTimeZoneId));
        Assert.Equal(new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc), usRow.NextDueUtc);

        var storedZone = ResolveTimeZone(usRow.CronTimeZoneId!);
        var localNextDue = TimeZoneInfo.ConvertTimeFromUtc(usRow.NextDueUtc!.Value, storedZone);
        Assert.Equal(9, localNextDue.Hour);
        Assert.Equal(0, localNextDue.Minute);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_Cron_UpdateFromUtcToUsEastern_SpringForward_PreservesNineAmLocal()
    {
        var now = new DateTimeOffset(2026, 3, 7, 12, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var usEastern = GetUsEasternTimeZone();

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);
        var grainId = GrainId.Create("test", "cron-spring-forward");
        const string reminderName = "cron-spring-forward";
        const string cron = "0 9 * * *";

        await service.RegisterOrUpdateReminder(grainId, reminderName, cron);
        await service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cron,
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            usEastern.Id);

        var row = await table.ReadRow(grainId, reminderName);
        Assert.NotNull(row);
        Assert.Equal(new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc), row.NextDueUtc);

        var afterFirstTick = row.NextDueUtc!.Value.AddSeconds(1);
        var nextDue = InvokePrivate<DateTime?>(service, "CalculateNextDue", row, afterFirstTick);

        Assert.Equal(new DateTime(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), nextDue);
        var localNextDue = TimeZoneInfo.ConvertTimeFromUtc(nextDue!.Value, usEastern);
        Assert.Equal(9, localNextDue.Hour);
        Assert.Equal(0, localNextDue.Minute);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_Cron_UpdateFromUtcToUsEastern_FallBack_PreservesNineAmLocal()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var usEastern = GetUsEasternTimeZone();

        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, timeProvider: timeProvider);
        var grainId = GrainId.Create("test", "cron-fall-back");
        const string reminderName = "cron-fall-back";
        const string cron = "0 9 * * *";

        await service.RegisterOrUpdateReminder(grainId, reminderName, cron);
        await service.RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cron,
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            usEastern.Id);

        var row = await table.ReadRow(grainId, reminderName);
        Assert.NotNull(row);
        Assert.Equal(new DateTime(2026, 10, 31, 13, 0, 0, DateTimeKind.Utc), row.NextDueUtc);

        var afterFirstTick = row.NextDueUtc!.Value.AddSeconds(1);
        var nextDue = InvokePrivate<DateTime?>(service, "CalculateNextDue", row, afterFirstTick);

        Assert.Equal(new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc), nextDue);
        var localNextDue = TimeZoneInfo.ConvertTimeFromUtc(nextDue!.Value, usEastern);
        Assert.Equal(9, localNextDue.Hour);
        Assert.Equal(0, localNextDue.Minute);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WhenServiceStopped_Throws()
    {
        var service = CreateOperationalService(statusName: "Stopped");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RegisterOrUpdateReminder(
                GrainId.Create("test", "stopped"),
                "stopped",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                ReminderPriority.Normal,
                MissedReminderAction.Skip));
    }

    [Fact]
    public async Task GetReminderAndGetReminders_ReturnStoredRows()
    {
        var grainId = GrainId.Create("test", "grain");
        var table = new InMemoryReminderTable();
        table.Seed(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "a",
            StartAt = DateTime.UtcNow,
            NextDueUtc = DateTime.UtcNow.AddMinutes(1),
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "1",
        });
        table.Seed(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "b",
            StartAt = DateTime.UtcNow,
            NextDueUtc = DateTime.UtcNow.AddMinutes(2),
            Period = TimeSpan.FromMinutes(2),
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.Notify,
            ETag = "2",
        });

        var service = CreateOperationalService(reminderTable: table);
        var single = await service.GetReminder(grainId, "a");
        var all = await service.GetReminders(grainId);
        var missing = await service.GetReminder(grainId, "missing");

        Assert.NotNull(single);
        Assert.Equal("a", single.ReminderName);
        Assert.Equal(2, all.Count);
        Assert.Null(missing);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WhenBooting_WaitsForInitialization()
    {
        var service = CreateOperationalService(statusName: "Booting");
        var started = GetFieldValue<TaskCompletionSource<bool>>(service, "_startedTask");
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            started.TrySetResult(true);
        });

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "booting"),
            "booting",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1),
            ReminderPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Equal("booting", reminder.ReminderName);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WhenBootingAndAlreadyStarted_DoesNotWait()
    {
        var service = CreateOperationalService(statusName: "Booting");
        var started = GetFieldValue<TaskCompletionSource<bool>>(service, "_startedTask");
        started.TrySetResult(true);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "booting-fast"),
            "booting-fast",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1),
            ReminderPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Equal("booting-fast", reminder.ReminderName);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WhenStatusIsUnknown_Throws()
    {
        var service = CreateOperationalService();
        var statusField = FindField(service.GetType(), "status");
        Assert.NotNull(statusField);
        statusField!.SetValue(service, Enum.ToObject(statusField.FieldType, 999));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterOrUpdateReminder(
                GrainId.Create("test", "unknown-status"),
                "unknown-status",
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(1),
                ReminderPriority.Normal,
                MissedReminderAction.Skip));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_DefaultOverload_UsesNormalAndSkip()
    {
        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "default-overload"),
            "default-overload",
            dueTime: TimeSpan.FromMilliseconds(5),
            period: TimeSpan.FromSeconds(1));

        var persisted = await table.ReadRow(GrainId.Create("test", "default-overload"), "default-overload");
        Assert.NotNull(persisted);
        Assert.Equal(ReminderPriority.Normal, persisted.Priority);
        Assert.Equal(MissedReminderAction.Skip, persisted.Action);
        Assert.Equal(ReminderPriority.Normal, reminder.Priority);
        Assert.Equal(MissedReminderAction.Skip, reminder.Action);
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_DefaultCronOverload_UsesNormalAndSkip()
    {
        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table);

        var reminder = await service.RegisterOrUpdateReminder(
            GrainId.Create("test", "default-cron-overload"),
            "default-cron-overload",
            "*/5 * * * * *");

        var persisted = await table.ReadRow(GrainId.Create("test", "default-cron-overload"), "default-cron-overload");
        Assert.NotNull(persisted);
        Assert.Equal(ReminderPriority.Normal, persisted.Priority);
        Assert.Equal(MissedReminderAction.Skip, persisted.Action);
        Assert.Equal(ReminderPriority.Normal, reminder.Priority);
        Assert.Equal(MissedReminderAction.Skip, reminder.Action);
    }

    [Fact]
    public void GetInitialPollRetryDelay_ReturnsBoundedExponentialDelayWithJitter()
    {
        var first = InvokePrivateStatic<TimeSpan>("GetInitialPollRetryDelay", (uint)1);
        var second = InvokePrivateStatic<TimeSpan>("GetInitialPollRetryDelay", (uint)2);
        var sixth = InvokePrivateStatic<TimeSpan>("GetInitialPollRetryDelay", (uint)6);

        Assert.InRange(first.TotalMilliseconds, 275, 500);
        Assert.InRange(second.TotalMilliseconds, 525, 750);
        Assert.InRange(sixth.TotalMilliseconds, 8_025, 8_250);
        Assert.True(second > first);
    }

    [Fact]
    public async Task OnRangeChange_WhenNotStarted_ReturnsCompletedTaskWithoutPolling()
    {
        var table = new InMemoryReminderTable();
        var service = CreateOperationalService(reminderTable: table, statusName: "Booting");
        var oldRange = new ConstantRingRange(true);
        var newRange = new ConstantRingRange(true);

        var task = service.OnRangeChange(oldRange, newRange, increased: false);
        await task;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, table.ReadRangeCalls);
    }

    [Fact]
    public async Task OnRangeChange_WhenStarted_TriggersPoll()
    {
        var table = new InMemoryReminderTable();
        table.Seed(new ReminderEntry
        {
            GrainId = GrainId.Create("test", "range-started"),
            ReminderName = "range-started",
            StartAt = DateTime.UtcNow,
            NextDueUtc = DateTime.UtcNow.AddSeconds(1),
            Period = TimeSpan.FromSeconds(5),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-range-started",
        });

        var fullRange = RangeFactory.CreateFullRange();
        var service = CreateOperationalService(reminderTable: table, statusName: "Started", ringRange: fullRange);

        await service.OnRangeChange(fullRange, fullRange, increased: true);

        Assert.True(table.ReadRangeCalls > 0);
    }

    [Fact]
    public async Task TryQueueReminder_WhenWriterIsCompleted_RemovesQueuedStateAndReturnsFalse()
    {
        var service = CreateOperationalService();
        var channel = Channel.CreateUnbounded<ReminderEntry>();
        channel.Writer.TryComplete();
        SetField(service, "_deliveryQueue", channel);

        var due = DateTime.UtcNow.AddSeconds(1);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "completed-writer"),
            ReminderName = "completed-writer",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromSeconds(5),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-closed",
        };

        var queued = InvokePrivate<bool>(service, "TryQueueReminder", entry, due.AddMinutes(1));

        Assert.False(queued);
        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public void RemoveOutOfRangeQueuedReminders_RemovesQueuedEntriesOutsideCurrentRange()
    {
        var service = CreateOperationalService(ringRange: new ConstantRingRange(true));
        var due = DateTime.UtcNow.AddSeconds(1);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "out-of-range"),
            ReminderName = "out-of-range",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromSeconds(5),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-range",
        };

        Assert.True(InvokePrivate<bool>(service, "TryQueueReminder", entry, due.AddMinutes(1)));
        Assert.Equal(1, GetEnqueuedCount(service));

        SetField(service, "<RingRange>k__BackingField", new ConstantRingRange(false));
        InvokePrivate<object>(service, "RemoveOutOfRangeQueuedReminders");

        Assert.Equal(0, GetEnqueuedCount(service));
    }

    [Fact]
    public void TryPrepareEntryForScheduling_WhenEntryIsNull_ReturnsFalse()
    {
        var service = CreateOperationalService();
        var method = typeof(AdaptiveReminderService).GetMethod("TryPrepareEntryForScheduling", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(service, [null!, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)])!;

        Assert.False(result);
    }

    [Fact]
    public void GetGrain_WhenReferenceActivatorIsMissing_Throws()
    {
        var service = CreateOperationalService();
        var method = typeof(AdaptiveReminderService).GetMethod("GetGrain", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Throws<TargetInvocationException>(() => method!.Invoke(service, [GrainId.Create("test", "grain-ref")]));
    }

    [Fact]
    public void SelectTopCandidatesForBucket_WhenSelectionLimitIsZero_ReturnsEmpty()
    {
        var selected = AdaptiveReminderService.SelectTopCandidatesForBucket(
            [new ReminderEntry
            {
                GrainId = GrainId.Create("test", "candidate"),
                ReminderName = "candidate",
                StartAt = DateTime.UtcNow,
                NextDueUtc = DateTime.UtcNow,
                Period = TimeSpan.FromSeconds(1),
                Priority = ReminderPriority.Normal,
                Action = MissedReminderAction.Skip,
                ETag = "etag-candidate",
            }],
            selectionLimit: 0,
            enablePriority: true);

        Assert.Empty(selected);
    }

    [Fact]
    public void AddCandidate_WhenSelectionLimitIsZero_DoesNotMutateQueue()
    {
        var candidate = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "candidate-limit-zero"),
            ReminderName = "candidate-limit-zero",
            StartAt = DateTime.UtcNow,
            NextDueUtc = DateTime.UtcNow,
            Period = TimeSpan.FromSeconds(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-candidate-limit-zero",
        };

        var method = typeof(AdaptiveReminderService).GetMethod("AddCandidate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method!.Invoke(null, [null!, candidate, 0, true]);
    }

    [Fact]
    public async Task ConstructorAndLifecycleParticipation_CoversLifecycleRegistration()
    {
        var reminderTable = new InMemoryReminderTable();
        var service = CreateConstructedService(reminderTable);
        var observers = CaptureLifecycleObservers(out var lifecycle);
        ((ILifecycleParticipant<ISiloLifecycle>)service).Participate(lifecycle);

        Assert.True(observers.TryGetValue(ServiceLifecycleStage.BecomeActive, out var becomeActiveObserver));
        Assert.True(observers.TryGetValue(ServiceLifecycleStage.Active, out var activeObserver));

        await becomeActiveObserver!.OnStart(CancellationToken.None);
        await activeObserver!.OnStart(CancellationToken.None);
        await becomeActiveObserver.OnStop(CancellationToken.None);
    }

    [Fact]
    public async Task LifecycleParticipation_WhenInitializeFails_RethrowsFromBecomeActiveStart()
    {
        var reminderTable = new InMemoryReminderTable
        {
            StartAsyncOverride = _ => throw new InvalidOperationException("init-fail"),
        };
        var service = CreateConstructedService(reminderTable);
        var observers = CaptureLifecycleObservers(out var lifecycle);

        ((ILifecycleParticipant<ISiloLifecycle>)service).Participate(lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            observers[ServiceLifecycleStage.BecomeActive].OnStart(CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleParticipation_WhenStartFails_RethrowsFromActiveStart()
    {
        var reminderTable = new InMemoryReminderTable();
        var service = CreateConstructedService(reminderTable);
        SetField(service, "_options", new ReminderOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            LookAheadWindow = TimeSpan.FromSeconds(2),
            BaseBucketSize = 8,
            InitializationTimeout = TimeSpan.Zero,
        });
        var observers = CaptureLifecycleObservers(out var lifecycle);

        ((ILifecycleParticipant<ISiloLifecycle>)service).Participate(lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            observers[ServiceLifecycleStage.Active].OnStart(CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleParticipation_WhenStopFails_RethrowsFromBecomeActiveStop()
    {
        var reminderTable = new InMemoryReminderTable
        {
            StopAsyncOverride = _ => throw new InvalidOperationException("stop-fail"),
        };
        var service = CreateConstructedService(reminderTable);
        var observers = CaptureLifecycleObservers(out var lifecycle);

        ((ILifecycleParticipant<ISiloLifecycle>)service).Participate(lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            observers[ServiceLifecycleStage.BecomeActive].OnStop(CancellationToken.None));
    }

    private static Dictionary<int, ILifecycleObserver> CaptureLifecycleObservers(out ISiloLifecycle lifecycle)
    {
        lifecycle = Substitute.For<ISiloLifecycle>();
        var observers = new Dictionary<int, ILifecycleObserver>();

        lifecycle
            .Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
            .Returns(callInfo =>
            {
                observers[(int)callInfo[1]] = (ILifecycleObserver)callInfo[2];
                return Substitute.For<IDisposable>();
            });

        return observers;
    }

    private static void QueueForProcessing(AdaptiveReminderService service, ReminderEntry entry, DateTime horizon)
    {
        var queued = InvokePrivate<bool>(service, "TryQueueReminder", entry, horizon);
        Assert.True(queued);

        // Keep the queue empty for deterministic checks. Queue state lives in _enqueuedReminders.
        var queue = GetFieldValue<Channel<ReminderEntry>>(service, "_deliveryQueue");
        _ = queue.Reader.TryRead(out _);
    }

    private static AdaptiveReminderService CreateOperationalService(
        ReminderOptions? options = null,
        InMemoryReminderTable? reminderTable = null,
        IAsyncTimer? pollTimer = null,
        IAsyncTimer? repairTimer = null,
        TimeProvider? timeProvider = null,
        IEnvironmentStatisticsProvider? environmentStatisticsProvider = null,
        IActivationWorkingSet? activationWorkingSet = null,
        IRingRange? ringRange = null,
        string statusName = "Started")
    {
        var service = (AdaptiveReminderService)RuntimeHelpers.GetUninitializedObject(typeof(AdaptiveReminderService));

        var effectiveOptions = options ?? new ReminderOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            LookAheadWindow = TimeSpan.FromMinutes(5),
            BaseBucketSize = 8,
            EnablePriority = true,
        };

        var effectiveTable = reminderTable ?? new InMemoryReminderTable();
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var effectiveRingRange = ringRange ?? RangeFactory.CreateFullRange();
        var effectivePollTimer = pollTimer ?? new SequenceTimer([false]);
        var effectiveRepairTimer = repairTimer ?? new SequenceTimer([false]);
        var effectiveStats = environmentStatisticsProvider ?? Substitute.For<IEnvironmentStatisticsProvider>();
        var effectiveWorkingSet = activationWorkingSet ?? Substitute.For<IActivationWorkingSet>();

        effectiveStats.GetEnvironmentStatistics().Returns(default(EnvironmentStatistics));
        effectiveWorkingSet.Count.Returns(1);

        var ringProvider = Substitute.For<IConsistentRingProvider>();
        ringProvider.GetMyRange().Returns(effectiveRingRange);
        ringProvider.SubscribeToRangeChangeEvents(Arg.Any<IRingRangeListener>()).Returns(true);
        ringProvider.UnSubscribeFromRangeChangeEvents(Arg.Any<IRingRangeListener>()).Returns(true);

        SetField(service, "_options", effectiveOptions);
        SetField(service, "_logger", NullLogger<AdaptiveReminderService>.Instance);
        SetField(service, "_reminderTable", effectiveTable);
        SetField(service, "_pollTimer", effectivePollTimer);
        SetField(service, "_repairTimer", effectiveRepairTimer);
        SetField(service, "_timeProvider", effectiveTimeProvider);
        SetField(service, "_environmentStatisticsProvider", effectiveStats);
        SetField(service, "_activationWorkingSet", effectiveWorkingSet);
        SetField(service, "_deliveryQueue", Channel.CreateUnbounded<ReminderEntry>());
        SetField(service, "_workerTasks", new List<Task>());
        SetField(service, "_cronCache", new ConcurrentDictionary<string, ReminderCronExpression>(StringComparer.Ordinal));
        SetField(service, "_startedTask", new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        var enqueuedField = FindField(typeof(AdaptiveReminderService), "_enqueuedReminders");
        Assert.NotNull(enqueuedField);
        SetField(service, "_enqueuedReminders", Activator.CreateInstance(enqueuedField!.FieldType)!);

        SetField(service, "ring", ringProvider);
        SetField(service, "Logger", NullLogger.Instance);
        SetField(service, "typeName", nameof(AdaptiveReminderService));
        SetField(service, "<StoppedCancellationTokenSource>k__BackingField", CreateStoppedCancellationTokenSource());
        SetField(service, "<RingRange>k__BackingField", effectiveRingRange);
        SetField(service, "<RangeSerialNumber>k__BackingField", 0);
        SetServiceStatus(service, statusName);

        return service;
    }

    private static AdaptiveReminderService CreateConstructedService(InMemoryReminderTable reminderTable)
    {
        var timerFactory = Substitute.For<IAsyncTimerFactory>();
        timerFactory.Create(Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns(_ => new SequenceTimer([false]));

        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1));
        localSiloDetails.Name.Returns("adaptive-reminders-test");
        localSiloDetails.DnsHostName.Returns("localhost");

        var ringProvider = Substitute.For<IConsistentRingProvider>();
        ringProvider.GetMyRange().Returns(RangeFactory.CreateFullRange());
        ringProvider.SubscribeToRangeChangeEvents(Arg.Any<IRingRangeListener>()).Returns(true);
        ringProvider.UnSubscribeFromRangeChangeEvents(Arg.Any<IRingRangeListener>()).Returns(true);

        var interfaceTypeResolver = new GrainInterfaceTypeResolver(
            [new FixedGrainInterfaceTypeProvider()],
            typeConverter: null!);

        var grainReferenceActivator = new GrainReferenceActivator(
            Substitute.For<IServiceProvider>(),
            Array.Empty<IGrainReferenceActivatorProvider>());

        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSiloDetails: localSiloDetails,
            loggerFactory: NullLoggerFactory.Instance,
            schedulingOptions: Options.Create(new SchedulingOptions()),
            grainReferenceActivator: grainReferenceActivator,
            timerRegistry: null!,
            activations: new ActivationDirectory());

        var options = new ReminderOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            LookAheadWindow = TimeSpan.FromSeconds(2),
            BaseBucketSize = 8,
            InitializationTimeout = TimeSpan.FromSeconds(5),
        };

        var stats = Substitute.For<IEnvironmentStatisticsProvider>();
        stats.GetEnvironmentStatistics().Returns(default(EnvironmentStatistics));
        var activationWorkingSet = Substitute.For<IActivationWorkingSet>();
        activationWorkingSet.Count.Returns(1);

        return new AdaptiveReminderService(
            referenceActivator: grainReferenceActivator,
            interfaceTypeResolver: interfaceTypeResolver,
            reminderTable: reminderTable,
            asyncTimerFactory: timerFactory,
            reminderOptions: Options.Create(options),
            timeProvider: TimeProvider.System,
            environmentStatisticsProvider: stats,
            activationWorkingSet: activationWorkingSet,
            ringProvider: ringProvider,
            shared: shared);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Owned by the test service instance via StoppedCancellationTokenSource.")]
    private static CancellationTokenSource CreateStoppedCancellationTokenSource() => new();

    private static void SetServiceStatus(AdaptiveReminderService service, string statusName)
    {
        var statusField = FindField(service.GetType(), "status");
        Assert.NotNull(statusField);
        var enumValue = Enum.Parse(statusField!.FieldType, statusName);
        statusField.SetValue(service, enumValue);
    }

    private static string GetServiceStatusName(AdaptiveReminderService service)
    {
        var statusField = FindField(service.GetType(), "status");
        Assert.NotNull(statusField);
        var value = statusField!.GetValue(service);
        Assert.NotNull(value);
        return value!.ToString()!;
    }

    private static int GetEnqueuedCount(AdaptiveReminderService service)
    {
        var enqueued = GetFieldValue<object>(service, "_enqueuedReminders");
        var countProperty = enqueued.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);
        return (int)countProperty!.GetValue(enqueued)!;
    }

    private static T InvokePrivate<T>(AdaptiveReminderService service, string methodName, params object[] args)
    {
        var method = typeof(AdaptiveReminderService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(service, args);
        return result is null ? default! : (T)result;
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(AdaptiveReminderService).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(null, args);
        return result is null ? default! : (T)result;
    }

    private static async Task InvokePrivateAsync(AdaptiveReminderService service, string methodName, params object[] args)
    {
        var method = typeof(AdaptiveReminderService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(service, args);
        if (result is Task task)
        {
            await task;
        }
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = FindField(instance.GetType(), fieldName);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static T GetFieldValue<T>(object instance, string fieldName)
    {
        var field = FindField(instance.GetType(), fieldName);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static TimeZoneInfo GetUsEasternTimeZone()
        => ResolveTimeZone("America/New_York", "Eastern Standard Time");

    private static TimeZoneInfo ResolveTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            if (TryFindTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException($"Could not resolve any of the requested time zones: {string.Join(", ", ids)}.");
    }

    private static bool TryFindTimeZoneById(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                return TryFindTimeZoneById(windowsId, out zone);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                return TryFindTimeZoneById(ianaId, out zone);
            }

            zone = null!;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = null!;
            return false;
        }
    }

    private sealed class ConstantRingRange(bool value) : IRingRange
    {
        public bool InRange(uint _) => value;
    }

    private sealed class FixedGrainInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            if (type == typeof(IRemindable))
            {
                grainInterfaceType = GrainInterfaceType.Create("AdaptiveReminderServiceTests.IRemindable");
                return true;
            }

            grainInterfaceType = default;
            return false;
        }
    }

    private sealed class SequenceTimer(IEnumerable<bool> ticks) : IAsyncTimer
    {
        private readonly Queue<bool> _ticks = new Queue<bool>(ticks);

        public Task<bool> NextTick(TimeSpan? overrideDelay = default)
            => Task.FromResult(_ticks.Count > 0 && _ticks.Dequeue());

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryReminderTable : IReminderTable
    {
        private readonly ConcurrentDictionary<(GrainId GrainId, string Name), ReminderEntry> _entries = new();
        private int _versionCounter;

        public Func<uint, uint, Task<ReminderTableData>>? ReadRowsRangeOverride { get; set; }
        public Func<GrainId, string, Task<ReminderEntry?>>? ReadRowOverride { get; set; }
        public Func<GrainId, string, string, Task<bool>>? RemoveRowOverride { get; set; }
        public Func<ReminderEntry, Task<string>>? UpsertRowOverride { get; set; }
        public Func<CancellationToken, Task>? StartAsyncOverride { get; set; }
        public Func<CancellationToken, Task>? StopAsyncOverride { get; set; }

        public int ReadRangeCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int StopCalls { get; private set; }

        public void Seed(ReminderEntry entry)
        {
            _entries[(entry.GrainId, entry.ReminderName)] = Clone(entry);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
            => StartAsyncOverride is not null ? StartAsyncOverride(cancellationToken) : Task.CompletedTask;

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            var rows = _entries.Values
                .Where(entry => entry.GrainId.Equals(grainId))
                .Select(Clone)
                .ToList();
            return Task.FromResult(new ReminderTableData(rows));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            ReadRangeCalls++;
            if (ReadRowsRangeOverride is not null)
            {
                return ReadRowsRangeOverride(begin, end);
            }

            bool InRange(uint value)
                => begin < end ? value > begin && value <= end : value > begin || value <= end;

            var rows = _entries.Values
                .Where(entry => InRange(entry.GrainId.GetUniformHashCode()))
                .Select(Clone)
                .ToList();
            return Task.FromResult(new ReminderTableData(rows));
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            if (ReadRowOverride is not null)
            {
                return ReadRowOverride(grainId, reminderName)
                    .ContinueWith(static t => t.Result is null ? null! : Clone(t.Result), TaskScheduler.Default);
            }

            return Task.FromResult(
                _entries.TryGetValue((grainId, reminderName), out var entry)
                    ? Clone(entry)
                    : null!);
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            UpsertCalls++;
            if (UpsertRowOverride is not null)
            {
                return UpsertRowOverride(entry);
            }

            var cloned = Clone(entry);
            cloned.ETag = $"v{Interlocked.Increment(ref _versionCounter)}";
            _entries[(cloned.GrainId, cloned.ReminderName)] = cloned;
            return Task.FromResult(cloned.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            RemoveCalls++;
            if (RemoveRowOverride is not null)
            {
                return RemoveRowOverride(grainId, reminderName, eTag);
            }

            if (!_entries.TryGetValue((grainId, reminderName), out var existing))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(existing.ETag, eTag, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_entries.TryRemove((grainId, reminderName), out _));
        }

        public Task TestOnlyClearTable()
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return StopAsyncOverride is not null ? StopAsyncOverride(cancellationToken) : Task.CompletedTask;
        }

        private static ReminderEntry Clone(ReminderEntry entry)
            => new()
            {
                GrainId = entry.GrainId,
                ReminderName = entry.ReminderName,
                StartAt = entry.StartAt,
                Period = entry.Period,
                ETag = entry.ETag,
                CronExpression = entry.CronExpression,
                CronTimeZoneId = entry.CronTimeZoneId,
                NextDueUtc = entry.NextDueUtc,
                LastFireUtc = entry.LastFireUtc,
                Priority = entry.Priority,
                Action = entry.Action,
            };
    }
}
