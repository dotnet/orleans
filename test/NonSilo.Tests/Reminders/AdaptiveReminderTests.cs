#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Statistics;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class AdaptiveReminderTests
{
    [Fact]
    public async Task StartInBackground_WhenInitialPollFails_ThrowsAndFaultsStartedTask()
    {
        using var stoppedCts = new CancellationTokenSource();
        var service = CreateServiceForInternals(new ReminderOptions());
        SetField(service, "_logger", NullLogger<AdaptiveReminderService>.Instance);
        SetField(service, "_timeProvider", TimeProvider.System);
        SetField(service, "_environmentStatisticsProvider", Substitute.For<IEnvironmentStatisticsProvider>());
        SetField(service, "_activationWorkingSet", Substitute.For<IActivationWorkingSet>());
        SetField(service, "_reminderTable", Substitute.For<IReminderTable>());
        SetField(service, "<StoppedCancellationTokenSource>k__BackingField", stoppedCts);
        SetField(service, "_startedTask", new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        var exception = await Assert.ThrowsAsync<OrleansException>(() => InvokePrivateAsync(service, "StartInBackground"));
        Assert.Contains("failed initial poll", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal((uint)3, GetFieldValue<uint>(service, "_initialReadCallCount"));
        Assert.True(GetFieldValue<TaskCompletionSource<bool>>(service, "_startedTask").Task.IsFaulted);
    }

    [Fact]
    public void CalculateAdaptiveBucketSize_UsesDocumentedFormula()
    {
        // 1024 * max(1,16/4) * max(0.25,1-0.6) * min(1,50000/40000)
        // = 1024 * 4 * 0.4 * 1 = 1638.4 => 1638 (rounded away from zero)
        var result = AdaptiveReminderService.CalculateAdaptiveBucketSize(
            baseBucketSize: 1024u,
            processorCount: 16,
            memoryLoadFraction: 0.6,
            activeGrainCount: 40_000);

        Assert.Equal(1638, result);
    }

    [Theory]
    [InlineData(1024u, 16, 0.0, 1_000, 4096)]
    [InlineData(1024u, 16, 0.95, 100_000, 512)]
    [InlineData(10u, 3, 0.4, 0, 6)]
    [InlineData(1u, 1, 1.0, 5_000_000, 1)]
    public void CalculateAdaptiveBucketSize_HandlesLoadAndBounds(
        uint baseBucketSize,
        int processorCount,
        double memoryLoadFraction,
        int activeGrainCount,
        int expected)
    {
        var result = AdaptiveReminderService.CalculateAdaptiveBucketSize(
            baseBucketSize,
            processorCount,
            memoryLoadFraction,
            activeGrainCount);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReminderEntry_ToIGrainReminder_ExposesAdaptiveMetadata()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = "0 */5 * * * *",
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(entry.ReminderName, reminder.ReminderName);
        Assert.Equal(entry.CronExpression, reminder.CronExpression);
        Assert.Equal(entry.Priority, reminder.Priority);
        Assert.Equal(entry.Action, reminder.Action);
    }

    [Fact]
    public void ReminderEntry_ToIGrainReminder_NormalizesNullCronExpression()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "key"),
            ReminderName = "rem",
            ETag = "etag",
            CronExpression = null,
        };

        var reminder = entry.ToIGrainReminder();

        Assert.Equal(string.Empty, reminder.CronExpression);
    }

    [Fact]
    public void CompareReminderEntries_WhenPriorityEnabled_OrdersByPriorityThenDue()
    {
        var service = CreateServiceForInternals(new ReminderOptions { EnablePriority = true });
        var critical = CreateEntry("critical", ReminderPriority.High, new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc));
        var normal = CreateEntry("normal", ReminderPriority.Normal, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));

        var result = InvokePrivate<int>(service, "CompareReminderEntries", critical, normal);

        Assert.True(result < 0);
    }

    [Fact]
    public void CompareReminderEntries_WhenPriorityDisabled_OrdersByDueTime()
    {
        var service = CreateServiceForInternals(new ReminderOptions { EnablePriority = false });
        var critical = CreateEntry("critical", ReminderPriority.High, new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc));
        var normal = CreateEntry("normal", ReminderPriority.Normal, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));

        var result = InvokePrivate<int>(service, "CompareReminderEntries", critical, normal);

        Assert.True(result > 0);
    }

    [Fact]
    public void CalculateNextDue_IntervalSkipsMissedTicks()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "interval",
            StartAt = now.AddMinutes(-10),
            Period = TimeSpan.FromSeconds(30),
            NextDueUtc = now.AddSeconds(-75),
        };

        var next = InvokePrivate<DateTime?>(service, "CalculateNextDue", entry, now);

        Assert.Equal(now.AddSeconds(15), next);
    }

    [Fact]
    public void CalculateNextDue_CronReturnsNextFutureTick()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 3, DateTimeKind.Utc);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "cron",
            CronExpression = "*/5 * * * * *",
            Period = TimeSpan.Zero,
            StartAt = now,
        };

        var next = InvokePrivate<DateTime?>(service, "CalculateNextDue", entry, now);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 5, DateTimeKind.Utc), next);
    }

    [Fact]
    public void CalculateNextDue_InvalidIntervalWithoutCron_ReturnsNull()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "invalid",
            StartAt = now,
            Period = TimeSpan.Zero,
        };

        var next = InvokePrivate<DateTime?>(service, "CalculateNextDue", entry, now);

        Assert.Null(next);
    }

    [Fact]
    public void TryPrepareEntryForScheduling_NormalizesStateAndConvertsToUtc()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var horizon = now.AddMinutes(5);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "prepare",
            StartAt = DateTime.SpecifyKind(now.AddMinutes(1), DateTimeKind.Unspecified),
            NextDueUtc = null,
            Period = TimeSpan.FromMinutes(1),
            Priority = (ReminderPriority)255,
            Action = (MissedReminderAction)255,
        };

        var shouldSchedule = InvokePrivate<bool>(service, "TryPrepareEntryForScheduling", entry, now, horizon);

        Assert.True(shouldSchedule);
        Assert.Equal(ReminderPriority.Normal, entry.Priority);
        Assert.Equal(MissedReminderAction.Skip, entry.Action);
        Assert.Equal(DateTimeKind.Utc, entry.NextDueUtc!.Value.Kind);
    }

    [Fact]
    public void TryPrepareEntryForScheduling_RejectsEntriesWithoutAnySchedule()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var horizon = now.AddMinutes(5);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "bad",
            Period = TimeSpan.Zero,
            CronExpression = null,
        };

        var shouldSchedule = InvokePrivate<bool>(service, "TryPrepareEntryForScheduling", entry, now, horizon);

        Assert.False(shouldSchedule);
    }

    [Fact]
    public void TryPrepareEntryForScheduling_RejectsEntriesOutsideLookAheadWindow()
    {
        var service = CreateServiceForInternals();
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var horizon = now.AddMinutes(5);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "future",
            StartAt = now.AddMinutes(10),
            NextDueUtc = now.AddMinutes(10),
            Period = TimeSpan.FromMinutes(1),
        };

        var shouldSchedule = InvokePrivate<bool>(service, "TryPrepareEntryForScheduling", entry, now, horizon);

        Assert.False(shouldSchedule);
    }

    [Fact]
    public void TryQueueReminder_DeduplicatesSameReminderIdentity()
    {
        var service = CreateServiceForInternals();
        SetField(service, "_deliveryQueue", Channel.CreateUnbounded<ReminderEntry>());

        var enqueuedFieldType = typeof(AdaptiveReminderService)
            .GetField("_enqueuedReminders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType;
        SetField(service, "_enqueuedReminders", Activator.CreateInstance(enqueuedFieldType)!);

        var due = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "dedupe",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromMinutes(1),
        };

        var first = InvokePrivate<bool>(service, "TryQueueReminder", entry, due.AddMinutes(1));
        var second = InvokePrivate<bool>(service, "TryQueueReminder", entry, due.AddMinutes(1));

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void TryQueueReminder_QueuesUpdatedReminderForSameIdentity()
    {
        var service = CreateServiceForInternals();
        var queue = Channel.CreateUnbounded<ReminderEntry>();
        SetField(service, "_deliveryQueue", queue);

        var enqueuedFieldType = typeof(AdaptiveReminderService)
            .GetField("_enqueuedReminders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType;
        SetField(service, "_enqueuedReminders", Activator.CreateInstance(enqueuedFieldType)!);

        var due = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc);
        var firstEntry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "update",
            ETag = "etag-1",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromMinutes(1),
        };

        var updatedEntry = new ReminderEntry
        {
            GrainId = firstEntry.GrainId,
            ReminderName = firstEntry.ReminderName,
            ETag = "etag-2",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromMinutes(2),
            Action = MissedReminderAction.Notify,
        };

        var first = InvokePrivate<bool>(service, "TryQueueReminder", firstEntry, due.AddMinutes(1));
        var second = InvokePrivate<bool>(service, "TryQueueReminder", updatedEntry, due.AddMinutes(1));
        var third = InvokePrivate<bool>(service, "TryQueueReminder", updatedEntry, due.AddMinutes(1));

        Assert.True(first);
        Assert.True(second);
        Assert.False(third);

        Assert.True(queue.Reader.TryRead(out var queuedFirst));
        Assert.True(queue.Reader.TryRead(out var queuedSecond));
        Assert.Equal("etag-1", queuedFirst.ETag);
        Assert.Equal("etag-2", queuedSecond.ETag);
    }

    [Fact]
    public void TryQueueReminder_RejectsEntriesOutsideHorizon()
    {
        var service = CreateServiceForInternals();
        SetField(service, "_deliveryQueue", Channel.CreateUnbounded<ReminderEntry>());

        var enqueuedFieldType = typeof(AdaptiveReminderService)
            .GetField("_enqueuedReminders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType;
        SetField(service, "_enqueuedReminders", Activator.CreateInstance(enqueuedFieldType)!);

        var due = new DateTime(2026, 1, 1, 10, 10, 0, DateTimeKind.Utc);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "horizon",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromMinutes(1),
        };

        var queued = InvokePrivate<bool>(service, "TryQueueReminder", entry, due.AddMinutes(-1));

        Assert.False(queued);
    }

    [Fact]
    public void CompareReminderEntries_WhenPriorityAndDueEqual_OrdersByReminderName()
    {
        var service = CreateServiceForInternals(new ReminderOptions { EnablePriority = true });
        var due = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var left = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "a",
            Priority = ReminderPriority.Normal,
            NextDueUtc = due,
            StartAt = due,
            Period = TimeSpan.FromMinutes(1),
        };

        var right = new ReminderEntry
        {
            GrainId = left.GrainId,
            ReminderName = "b",
            Priority = ReminderPriority.Normal,
            NextDueUtc = due,
            StartAt = due,
            Period = TimeSpan.FromMinutes(1),
        };

        var result = InvokePrivate<int>(service, "CompareReminderEntries", left, right);

        Assert.True(result < 0);
    }

    [Fact]
    public void SelectTopCandidatesForBucket_ProcessesFiveMillionNearSimultaneousReminders()
    {
        const int totalReminders = 5_000_000;
        const int selectionLimit = 2_048;
        var baseDueUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var selected = AdaptiveReminderService.SelectTopCandidatesForBucket(
            CreateBurstCandidates(totalReminders, baseDueUtc),
            selectionLimit,
            enablePriority: true);

        Assert.Equal(selectionLimit, selected.Count);
        Assert.All(selected, entry => Assert.Equal(ReminderPriority.High, entry.Priority));
        Assert.Equal(baseDueUtc, selected[0].NextDueUtc);
        Assert.All(selected, entry => Assert.InRange(entry.NextDueUtc!.Value, baseDueUtc, baseDueUtc.AddMinutes(2)));
        Assert.True(IsOrderedByPriorityAndDue(selected, enablePriority: true));
    }

    [Fact]
    public void SelectTopCandidatesForBucket_ClonesSelectedEntriesFromReusableSequence()
    {
        var baseDueUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var selected = AdaptiveReminderService.SelectTopCandidatesForBucket(
            CreateBurstCandidates(count: 10_000, baseDueUtc),
            selectionLimit: 64,
            enablePriority: true);

        Assert.Equal(64, selected.Count);
        Assert.All(selected, entry => Assert.Equal(ReminderPriority.High, entry.Priority));
        Assert.All(selected, entry => Assert.InRange(entry.NextDueUtc!.Value, baseDueUtc, baseDueUtc.AddMinutes(2)));
        Assert.True(selected.Select(entry => entry.NextDueUtc!.Value).Distinct().Count() > 1);
        Assert.True(IsOrderedByPriorityAndDue(selected, enablePriority: true));
    }

    [Fact]
    public void SelectTopCandidatesForBucket_EnumeratesInputOnce()
    {
        var baseDueUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var enumerable = new SinglePassEnumerable(CreateBurstCandidates(100_000, baseDueUtc));

        var selected = AdaptiveReminderService.SelectTopCandidatesForBucket(
            enumerable,
            selectionLimit: 256,
            enablePriority: true);

        Assert.Equal(1, enumerable.EnumerationCount);
        Assert.Equal(256, selected.Count);
        Assert.True(IsOrderedByPriorityAndDue(selected, enablePriority: true));
    }

    private static AdaptiveReminderService CreateServiceForInternals(ReminderOptions? options = null)
    {
        var service = (AdaptiveReminderService)RuntimeHelpers.GetUninitializedObject(typeof(AdaptiveReminderService));
        SetField(service, "_options", options ?? new ReminderOptions());
        SetField(service, "_cronCache", new ConcurrentDictionary<string, ReminderCronExpression>(StringComparer.Ordinal));
        return service;
    }

    private static ReminderEntry CreateEntry(string name, ReminderPriority priority, DateTime nextDueUtc)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Create("test", name),
            ReminderName = name,
            Priority = priority,
            Action = MissedReminderAction.Skip,
            NextDueUtc = nextDueUtc,
            StartAt = nextDueUtc,
            Period = TimeSpan.FromMinutes(1),
        };
    }

    private static T InvokePrivate<T>(AdaptiveReminderService service, string methodName, params object[] args)
    {
        var method = typeof(AdaptiveReminderService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(service, args);

        if (result is null)
        {
            return default!;
        }

        return (T)result;
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

    private static IEnumerable<ReminderEntry> CreateBurstCandidates(int count, DateTime baseDueUtc)
    {
        const int poolSize = 1024;
        var grainIds = new GrainId[poolSize];
        var reminderNames = new string[poolSize];
        for (var i = 0; i < poolSize; i++)
        {
            grainIds[i] = GrainId.Create("stress", $"grain-{i}");
            reminderNames[i] = $"reminder-{i}";
        }

        var reusable = new ReminderEntry
        {
            ETag = "stress",
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.Skip,
            CronExpression = null,
        };

        for (var i = 0; i < count; i++)
        {
            var slot = i & (poolSize - 1);
            var due = baseDueUtc.AddSeconds(i % 121); // burst inside ~2 minutes

            reusable.GrainId = grainIds[slot];
            reusable.ReminderName = reminderNames[slot];
            reusable.Priority = i % 10 == 0 ? ReminderPriority.High : ReminderPriority.Normal;
            reusable.StartAt = due;
            reusable.NextDueUtc = due;
            reusable.LastFireUtc = null;

            yield return reusable;
        }
    }

    private static bool IsOrderedByPriorityAndDue(List<ReminderEntry> entries, bool enablePriority)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            var previous = entries[i - 1];
            var current = entries[i];

            var previousPriority = enablePriority ? previous.Priority : ReminderPriority.Normal;
            var currentPriority = enablePriority ? current.Priority : ReminderPriority.Normal;
            var previousDue = previous.NextDueUtc ?? previous.StartAt;
            var currentDue = current.NextDueUtc ?? current.StartAt;

            if (previousPriority < currentPriority)
            {
                return false;
            }

            if (previousPriority == currentPriority && previousDue > currentDue)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class SinglePassEnumerable(IEnumerable<ReminderEntry> source) : IEnumerable<ReminderEntry>
    {
        private readonly IEnumerable<ReminderEntry> _source = source;

        public int EnumerationCount { get; private set; }

        public IEnumerator<ReminderEntry> GetEnumerator()
        {
            EnumerationCount++;
            return _source.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
