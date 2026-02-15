#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
[TestCategory("Stress")]
public class ReminderStressTests
{
    [Fact]
    public async Task ListFilteredAsync_HighLoad_200K_Reminders_PagesCorrectly()
    {
        const int totalReminders = 200_000;
        const int pageSize = 2_048;

        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var reminders = CreateSyntheticReminders(totalReminders, now.UtcDateTime);
        var table = new SingleSegmentReminderTable(reminders);
        var grain = new ReminderManagementGrain(table, timeProvider);

        var filter = new ReminderQueryFilter
        {
            Priority = ReminderPriority.High,
            ScheduleKind = ReminderScheduleKind.Cron,
            Status = ReminderQueryStatus.Upcoming,
        };

        var expectedCount = reminders.Count(reminder =>
            reminder.Priority == ReminderPriority.High
            && !string.IsNullOrWhiteSpace(reminder.CronExpression)
            && (reminder.NextDueUtc ?? reminder.StartAt) > now.UtcDateTime);

        var observedCount = 0;
        var seenReminderNames = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        do
        {
            var page = await grain.ListFilteredAsync(filter, pageSize, continuationToken);
            Assert.InRange(page.Reminders.Count, 0, pageSize);

            foreach (var reminder in page.Reminders)
            {
                observedCount++;
                Assert.True(seenReminderNames.Add(reminder.ReminderName), $"Duplicate reminder '{reminder.ReminderName}' in filtered paging output.");
                Assert.Equal(ReminderPriority.High, reminder.Priority);
                Assert.False(string.IsNullOrWhiteSpace(reminder.CronExpression));
                Assert.True((reminder.NextDueUtc ?? reminder.StartAt) > now.UtcDateTime);
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        Assert.Equal(expectedCount, observedCount);
        Assert.True(table.RangeReadCallCount >= 128, "Expected at least one full hash-range scan.");
    }

    [Fact]
    public async Task Iterator_HighLoad_StreamsOneMillionReminders()
    {
        const int totalReminders = 1_000_000;
        const int pageSize = 4_096;

        var management = new SyntheticPagedReminderManagementGrain(totalReminders);
        var iterator = new ReminderIterator(management);

        long observed = 0;
        await foreach (var _ in iterator.EnumerateAllAsync(pageSize))
        {
            observed++;
        }

        Assert.Equal(totalReminders, observed);
        Assert.True(management.ListAllCallCount > 1);
    }

    [Fact]
    public async Task Iterator_ExtremeLoad_StreamsFiveMillionReminders_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ORLEANS_REMINDER_STRESS_5M"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const int totalReminders = 5_000_000;
        const int pageSize = 8_192;

        var management = new SyntheticPagedReminderManagementGrain(totalReminders);
        var iterator = new ReminderIterator(management);

        long observed = 0;
        await foreach (var _ in iterator.EnumerateAllAsync(pageSize))
        {
            observed++;
        }

        Assert.Equal(totalReminders, observed);
        Assert.True(management.ListAllCallCount > 1);
    }

    private static List<ReminderEntry> CreateSyntheticReminders(int count, DateTime nowUtc)
    {
        var result = new List<ReminderEntry>(count);
        var sharedGrainId = GrainId.Create("stress", "scan");

        for (var i = 0; i < count; i++)
        {
            var due = nowUtc.AddSeconds((i % 120) - 60);
            result.Add(new ReminderEntry
            {
                GrainId = sharedGrainId,
                ReminderName = $"r-{i.ToString(CultureInfo.InvariantCulture)}",
                StartAt = due,
                NextDueUtc = due,
                LastFireUtc = due.AddSeconds(-1),
                Period = TimeSpan.FromMinutes(1),
                CronExpression = i % 2 == 0 ? "*/5 * * * * *" : null,
                Priority = (i % 3) switch
                {
                    0 => ReminderPriority.High,
                    1 => ReminderPriority.Normal,
                    _ => ReminderPriority.Normal,
                },
                Action = (i % 3) switch
                {
                    0 => MissedReminderAction.FireImmediately,
                    1 => MissedReminderAction.Skip,
                    _ => MissedReminderAction.Notify,
                },
            });
        }

        return result;
    }

    private sealed class SingleSegmentReminderTable(List<ReminderEntry> reminders) : IReminderTable
    {
        private static readonly ReminderTableData Empty = new([]);
        private readonly ReminderTableData _segmentData = new(reminders);
        public int RangeReadCallCount { get; private set; }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadCallCount++;
            return Task.FromResult(begin == uint.MaxValue ? _segmentData : Empty);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class SyntheticPagedReminderManagementGrain(int totalReminders) : IReminderManagementGrain
    {
        private const string TokenPrefix = "offset:";
        private readonly GrainId _sharedGrainId = GrainId.Create("stress", "iterator");
        public int ListAllCallCount { get; private set; }

        public Task<ReminderManagementPage> ListAllAsync(int pageSize = 256, string? continuationToken = null)
        {
            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            ListAllCallCount++;
            var offset = ParseOffset(continuationToken);
            if (offset >= totalReminders)
            {
                return Task.FromResult(new ReminderManagementPage { Reminders = [], ContinuationToken = null });
            }

            var take = Math.Min(pageSize, totalReminders - offset);
            var reminders = new List<ReminderEntry>(take);
            for (var i = 0; i < take; i++)
            {
                reminders.Add(new ReminderEntry
                {
                    GrainId = _sharedGrainId,
                    ReminderName = "bulk",
                    StartAt = DateTime.UnixEpoch,
                    NextDueUtc = DateTime.UnixEpoch,
                    Period = TimeSpan.FromMinutes(1),
                    Priority = ReminderPriority.Normal,
                    Action = MissedReminderAction.Skip,
                });
            }

            var nextOffset = offset + take;
            return Task.FromResult(new ReminderManagementPage
            {
                Reminders = reminders,
                ContinuationToken = nextOffset < totalReminders ? TokenPrefix + nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            });
        }

        public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null)
            => throw new NotSupportedException();

        public Task<ReminderManagementPage> ListDueInRangeAsync(DateTime fromUtcInclusive, DateTime toUtcInclusive, int pageSize = 256, string? continuationToken = null)
            => throw new NotSupportedException();

        public Task<ReminderManagementPage> ListFilteredAsync(ReminderQueryFilter filter, int pageSize = 256, string? continuationToken = null)
            => throw new NotSupportedException();

        public Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon) => throw new NotSupportedException();

        public Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId) => throw new NotSupportedException();

        public Task<int> CountAllAsync() => throw new NotSupportedException();

        public Task SetPriorityAsync(GrainId grainId, string name, ReminderPriority priority) => throw new NotSupportedException();

        public Task SetActionAsync(GrainId grainId, string name, MissedReminderAction action) => throw new NotSupportedException();

        public Task RepairAsync(GrainId grainId, string name) => throw new NotSupportedException();

        public Task DeleteAsync(GrainId grainId, string name) => throw new NotSupportedException();

        private static int ParseOffset(string? continuationToken)
        {
            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                return 0;
            }

            if (!continuationToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid continuation token format.", nameof(continuationToken));
            }

            var payload = continuationToken.AsSpan(TokenPrefix.Length);
            if (!int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            {
                throw new ArgumentException("Invalid continuation token payload.", nameof(continuationToken));
            }

            return offset;
        }
    }
}
