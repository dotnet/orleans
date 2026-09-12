#nullable enable
using System.Globalization;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
[TestCategory("Stress")]
public class ReminderStressTests
{
    [Fact]
    public async Task ListAllAsync_Skewed200KBucket_SmallPublicPageUsesBoundedProviderPages()
    {
        const int totalReminders = 200_000;
        const int pageSize = 1;

        var now = DateTime.UtcNow;
        var reminders = CreateSyntheticReminders(totalReminders, now);
        var table = new SingleSegmentReminderTable(reminders);
        var grain = new ReminderManagementGrain(table);

        var page = await grain.ListAllAsync(pageSize);

        Assert.Single(page.Reminders);
        Assert.NotNull(page.ContinuationToken);
        Assert.Equal(0, table.CompleteRangeReadCallCount);
        Assert.Equal(256, table.MaxRequestedRows);
        Assert.InRange(table.MaxReturnedRows, 0, 256);
        Assert.True(table.PagedRangeReadCallCount >= (int)Math.Ceiling(totalReminders / 256d));
    }

    [Fact]
    public async Task Iterator_HighLoad_StreamsOneMillionReminders()
    {
        const int totalReminders = 1_000_000;
        const int pageSize = 4_096;

        var management = new SyntheticPagedReminderManagementGrain(totalReminders);
        var iterator = new ReminderIterator(management);

        long observed = 0;
        await foreach (var _ in iterator.EnumerateAllAsync(pageSize, TestContext.Current.CancellationToken))
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
                CronExpression = i % 2 == 0 ? "*/5 * * * * *" : null!,
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

    private sealed class SingleSegmentReminderTable(List<ReminderEntry> reminders) : Orleans.AdvancedReminders.IReminderTable
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public int CompleteRangeReadCallCount { get; private set; }

        public int PagedRangeReadCallCount { get; private set; }

        public int MaxRequestedRows { get; private set; }

        public int MaxReturnedRows { get; private set; }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            CompleteRangeReadCallCount++;
            throw new InvalidOperationException("Management paging must use the bounded provider overload.");
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
        {
            PagedRangeReadCallCount++;
            MaxRequestedRows = Math.Max(MaxRequestedRows, maxRows);
            var range = RangeFactory.CreateRange(begin, end);
            if (reminders.Count == 0 || !range.InRange(reminders[0].GrainId))
            {
                return Task.FromResult(new ReminderTableData());
            }

            var offset = continuationToken is null ? 0 : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            var rows = reminders.Skip(offset).Take(maxRows).ToList();
            MaxReturnedRows = Math.Max(MaxReturnedRows, rows.Count);
            var nextOffset = offset + rows.Count;

            return Task.FromResult(new ReminderTableData(
                rows,
                nextOffset < reminders.Count ? nextOffset.ToString(CultureInfo.InvariantCulture) : null));
        }

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

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
                    ReminderName = $"bulk-{offset + i:0000000}",
                    StartAt = DateTime.UnixEpoch,
                    NextDueUtc = DateTime.UnixEpoch,
                    Period = TimeSpan.FromMinutes(1),
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

        public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<ReminderManagementPage> ListDueInRangeAsync(DateTime fromUtcInclusive, DateTime toUtcInclusive, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<ReminderManagementPage> ListFilteredAsync(ReminderQueryFilter filter, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId) => throw new NotSupportedException();
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
