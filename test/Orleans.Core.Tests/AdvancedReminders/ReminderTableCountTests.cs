#nullable enable
using System.Globalization;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using IReminderTableGrain = Orleans.AdvancedReminders.IReminderTableGrain;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class ReminderTableCountTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1_024)]
    [InlineData(34_900)]
    public async Task DefaultCount_UsesBoundedPagesAcrossTheWholeStore(int count)
    {
        var table = new PagedCountTable(count);

        var actual = await ((Orleans.AdvancedReminders.IReminderTable)table).GetCountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(count, actual);
        Assert.Equal(Math.Max(1, (count + 1_023) / 1_024), table.ReadCount);
    }

    [Fact]
    public async Task DefaultCount_ContinuesAfterAnEmptyPageWithAToken()
    {
        var table = new PagedCountTable(2) { ReturnEmptyFirstPage = true };

        Assert.Equal(2, await ((Orleans.AdvancedReminders.IReminderTable)table).GetCountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, table.ReadCount);
    }

    [Fact]
    public async Task DefaultCount_HonorsCancellationBeforeReading()
    {
        var table = new PagedCountTable(34_900);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((Orleans.AdvancedReminders.IReminderTable)table).GetCountAsync(new CancellationToken(canceled: true)));

        Assert.Equal(0, table.ReadCount);
    }

    [Fact]
    public async Task InMemoryCount_TracksRegistrationIdentityThroughUpdatesAndDeletes()
    {
        var table = new AdvancedReminderTableGrain();
        var first = new ReminderEntry { GrainId = GrainId.Create("count", "one"), ReminderName = "first" };
        var second = new ReminderEntry { GrainId = first.GrainId, ReminderName = "second" };
        var third = new ReminderEntry { GrainId = GrainId.Create("count", "two"), ReminderName = "third" };
        Assert.Equal(0, await table.GetCount());

        first.ETag = await table.UpsertRow(first);
        second.ETag = await table.UpsertRow(second);
        third.ETag = await table.UpsertRow(third);
        Assert.Equal(3, await table.GetCount());
        first.NextDueUtc = DateTime.UtcNow.AddDays(1);
        first.ETag = await table.UpsertRow(first);
        Assert.Equal(3, await table.GetCount());
        Assert.False(await table.RemoveRow(first.GrainId, first.ReminderName, "stale"));
        Assert.Equal(3, await table.GetCount());
        Assert.True(await table.RemoveRow(first.GrainId, first.ReminderName, first.ETag));
        Assert.Equal(2, await table.GetCount());
        await table.TestOnlyClearTable();
        Assert.Equal(0, await table.GetCount());
    }

    [Fact]
    public async Task InMemoryProxyCount_UsesTheGrainCount()
    {
        var factory = Substitute.For<IGrainFactory>();
        var grain = Substitute.For<IReminderTableGrain>();
        factory.GetGrain<IReminderTableGrain>(InMemoryReminderTable.ReminderTableGrainId).Returns(grain);
        grain.GetCount().Returns(Task.FromResult(34_900L));
        var table = new InMemoryReminderTable(factory);
        await table.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(34_900, await table.GetCountAsync(TestContext.Current.CancellationToken));
        await grain.Received(1).GetCount();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.GetCountAsync(new CancellationToken(canceled: true)));
        await grain.Received(1).GetCount();
    }

    private sealed class PagedCountTable(int count) : Orleans.AdvancedReminders.IReminderTable
    {
        public int ReadCount { get; private set; }
        public bool ReturnEmptyFirstPage { get; init; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
        {
            Assert.Equal(0u, begin);
            Assert.Equal(0u, end);
            Assert.Equal(1_024, maxRows);
            ReadCount++;
            if (ReturnEmptyFirstPage && continuationToken is null)
            {
                return Task.FromResult(new ReminderTableData([], "0"));
            }

            var offset = continuationToken is null ? 0 : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            var take = Math.Min(maxRows, count - offset);
            return Task.FromResult(new ReminderTableData(
                Enumerable.Range(0, take).Select(static _ => new ReminderEntry()),
                offset + take < count ? (offset + take).ToString(CultureInfo.InvariantCulture) : null));
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => throw new NotSupportedException("Count must use bounded reads.");
        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();
        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();
        public Task<string> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();
        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();
        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }
}
