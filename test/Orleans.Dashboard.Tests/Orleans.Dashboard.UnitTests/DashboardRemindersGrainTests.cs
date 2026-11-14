using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation.Grains;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;
using AdvancedReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using ClassicReminderEntry = Orleans.ReminderEntry;
using ClassicReminderTable = Orleans.IReminderTable;
using ClassicReminderTableData = Orleans.ReminderTableData;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;

namespace Orleans.Dashboard.UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public sealed class DashboardRemindersGrainTests
{
    [Fact]
    public async Task GetReminders_ReturnsOnlyClassicRows()
    {
        var classicStart = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        var advancedStart = classicStart.AddHours(-2);
        var advancedNextDue = classicStart.AddHours(-1);
        var advancedLastFire = classicStart.AddHours(-3);
        var serviceProvider = new ReminderServiceProvider(
            new ClassicReminderTableStub(
                new ClassicReminderEntry
                {
                    GrainId = GrainId.Create("classic-grain", "classic-key"),
                    ReminderName = "classic-reminder",
                    StartAt = classicStart,
                    Period = TimeSpan.FromHours(1),
                    ETag = "classic-etag",
                }),
            new AdvancedReminderTableStub(
                new AdvancedReminderEntry
                {
                    GrainId = GrainId.Create("advanced-grain", "advanced-key"),
                    ReminderName = "advanced-reminder",
                    StartAt = advancedStart,
                    Period = TimeSpan.Zero,
                    ETag = "advanced-etag",
                    CronExpression = "0 */5 * * * *",
                    CronTimeZoneId = "Europe/Paris",
                    NextDueUtc = advancedNextDue,
                    LastFireUtc = advancedLastFire,
                    Action = MissedReminderAction.FireImmediately,
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetReminders(1, 50, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(1, response.Count);
        var classic = Assert.Single(response.Reminders);
        Assert.Equal("classic-reminder", classic.Name);
    }

    [Fact]
    public async Task GetAdvancedReminders_ReturnsCronScheduleWithTimeZone()
    {
        var startAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);
        var nextDue = startAt.AddHours(1);
        var lastFire = startAt.AddHours(-1);
        var serviceProvider = new ReminderServiceProvider(
            new ClassicReminderTableStub(),
            new AdvancedReminderTableStub(
                new AdvancedReminderEntry
                {
                    GrainId = GrainId.Create("advanced-grain", "advanced-key"),
                    ReminderName = "advanced-reminder",
                    StartAt = startAt,
                    Period = TimeSpan.Zero,
                    ETag = "advanced-etag",
                    CronExpression = "0 */5 * * * *",
                    CronTimeZoneId = "Europe/Paris",
                    NextDueUtc = nextDue,
                    LastFireUtc = lastFire,
                    Action = MissedReminderAction.FireImmediately,
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetAdvancedReminders(1, 50, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(1, response.Count);
        var advanced = Assert.Single(response.Reminders);
        Assert.Equal("advanced-reminder", advanced.Name);
        Assert.Equal("0 */5 * * * *", advanced.CronExpression);
        Assert.Equal("Europe/Paris", advanced.CronTimeZoneId);
        Assert.Equal(nextDue, advanced.NextDueUtc);
        Assert.Equal(lastFire, advanced.LastFireUtc);
        Assert.Equal("FireImmediately", advanced.MissedAction);
    }

    [Fact]
    public async Task GetAdvancedReminders_ReturnsCronScheduleWithoutTimeZoneAsUtcDefault()
    {
        var startAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);
        var nextDue = startAt.AddHours(1);
        var serviceProvider = new ReminderServiceProvider(
            new ClassicReminderTableStub(),
            new AdvancedReminderTableStub(
                new AdvancedReminderEntry
                {
                    GrainId = GrainId.Create("advanced-grain", "utc-key"),
                    ReminderName = "advanced-utc-reminder",
                    StartAt = startAt,
                    CronExpression = "0 * * * * *",
                    NextDueUtc = nextDue,
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetAdvancedReminders(1, 50, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(1, response.Count);
        var utc = Assert.Single(response.Reminders);
        Assert.Equal("advanced-utc-reminder", utc.Name);
        Assert.Equal("0 * * * * *", utc.CronExpression);
        Assert.Empty(utc.CronTimeZoneId);
        Assert.Equal(nextDue, utc.NextDueUtc);
    }

    [Fact]
    public async Task GetAdvancedReminders_PaginatesIndependently()
    {
        var serviceProvider = new ReminderServiceProvider(
            new ClassicReminderTableStub(
                new ClassicReminderEntry
                {
                    GrainId = GrainId.Create("classic-grain", "classic-key"),
                    ReminderName = "classic-reminder",
                    StartAt = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
                }),
            new AdvancedReminderTableStub(
                new AdvancedReminderEntry
                {
                    GrainId = GrainId.Create("advanced-grain", "advanced-key"),
                    ReminderName = "advanced-reminder-1",
                    StartAt = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc),
                    NextDueUtc = new DateTime(2026, 7, 22, 11, 0, 0, DateTimeKind.Utc),
                },
                new AdvancedReminderEntry
                {
                    GrainId = GrainId.Create("advanced-grain", "advanced-key"),
                    ReminderName = "advanced-reminder-2",
                    StartAt = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
                    NextDueUtc = new DateTime(2026, 7, 22, 13, 0, 0, DateTimeKind.Utc),
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetAdvancedReminders(2, 1, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(2, response.Count);
        var reminder = Assert.Single(response.Reminders);
        Assert.Equal("advanced-reminder-2", reminder.Name);
    }

    [Fact]
    public async Task GetAdvancedReminders_UsesStoreCountWithoutReadingReminderRows()
    {
        var advancedReminderTable = new AdvancedReminderTableStub { StoredCount = 34_900 };
        var serviceProvider = new ReminderServiceProvider(new ClassicReminderTableStub(), advancedReminderTable);
        var management = new PagedAdvancedReminderManagementGrain();
        var grain = new DashboardRemindersGrain(serviceProvider with { Management = management });

        var first = (await grain.GetAdvancedReminders(1, 1, TestContext.Current.CancellationToken)).Value;
        var second = (await grain.GetAdvancedReminders(2, 1, TestContext.Current.CancellationToken)).Value;

        Assert.Equal("advanced-reminder-1", Assert.Single(first.Reminders).Name);
        Assert.Equal("advanced-reminder-2", Assert.Single(second.Reminders).Name);
        Assert.Equal(34_900, first.Count);
        Assert.Equal(34_900, second.Count);
        Assert.Equal(1, advancedReminderTable.CountReadCount);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(2, management.ListCallCount);
        Assert.Equal(0, advancedReminderTable.RangeReadCount);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(34_900L)]
    [InlineData(3_000_000_000L)]
    public async Task GetAdvancedReminders_RefreshesStoreCountAfterThirtySeconds(long storedCount)
    {
        var clock = new CountTimeProvider();
        var table = new AdvancedReminderTableStub { StoredCount = storedCount };
        var grain = new DashboardRemindersGrain(
            new ReminderServiceProvider(new ClassicReminderTableStub(), table, clock));

        var first = (await grain.GetAdvancedReminders(1, 1, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(storedCount, first.Count);
        table.StoredCount++;
        clock.Advance(TimeSpan.FromSeconds(29));
        var cached = (await grain.GetAdvancedReminders(2, 1, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(storedCount, cached.Count);
        Assert.Equal(1, table.CountReadCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = (await grain.GetAdvancedReminders(1, 1, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(storedCount + 1, refreshed.Count);
        Assert.Equal(2, table.CountReadCount);
    }

    [Fact]
    public async Task GetAdvancedReminders_CancelledRequestDoesNotReadCountOrPage()
    {
        var table = new AdvancedReminderTableStub();
        var management = new PagedAdvancedReminderManagementGrain();
        var grain = new DashboardRemindersGrain(
            new ReminderServiceProvider(new ClassicReminderTableStub(), table) { Management = management });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            grain.GetAdvancedReminders(1, 50, new CancellationToken(canceled: true)));

        Assert.Equal(0, table.CountReadCount);
        Assert.Equal(0, management.ListCallCount);
    }

    [Fact]
    public async Task GetAdvancedReminders_PageBeyondReplayLimitDoesNotScanTable()
    {
        var advancedReminderTable = new AdvancedReminderTableStub();
        var serviceProvider = new ReminderServiceProvider(new ClassicReminderTableStub(), advancedReminderTable);
        var management = new PagedAdvancedReminderManagementGrain();
        var grain = new DashboardRemindersGrain(serviceProvider with { Management = management });

        var response = (await grain.GetAdvancedReminders(int.MaxValue, 1, TestContext.Current.CancellationToken)).Value;

        Assert.Empty(response.Reminders);
        Assert.Equal(0, response.Count);
        Assert.False(response.HasMore);
        Assert.Equal(0, management.ListCallCount);
    }

    [Fact]
    public async Task GetAdvancedReminders_BoundsContinuationTokenCache()
    {
        var serviceProvider = new ReminderServiceProvider(
            new ClassicReminderTableStub(),
            new AdvancedReminderTableStub());
        var management = new PagedAdvancedReminderManagementGrain { MaxPageSize = 1 };
        var grain = new DashboardRemindersGrain(serviceProvider with { Management = management });

        for (var pageSize = 1; pageSize <= 65; pageSize++)
        {
            await grain.GetAdvancedReminders(1, pageSize, TestContext.Current.CancellationToken);
        }

        var callsBeforeEvictedPage = management.ListCallCount;
        var response = (await grain.GetAdvancedReminders(2, 1, TestContext.Current.CancellationToken)).Value;

        Assert.Equal("advanced-reminder-2", Assert.Single(response.Reminders).Name);
        Assert.Equal(callsBeforeEvictedPage + 2, management.ListCallCount);
    }

    [Fact]
    public async Task GetReminders_ReadsFullHashRange()
    {
        var classicReminderTable = new ClassicReminderTableStub();
        var serviceProvider = new ReminderServiceProvider(classicReminderTable, new AdvancedReminderTableStub());
        var grain = new DashboardRemindersGrain(serviceProvider);

        await grain.GetReminders(1, 50, TestContext.Current.CancellationToken);

        Assert.Equal(1, classicReminderTable.RangeReadCount);
        Assert.Equal(0u, classicReminderTable.BeginHash);
        Assert.Equal(0u, classicReminderTable.EndHash);
    }

    [Fact]
    public async Task AddAdvancedReminderDashboard_ExposesRegistrationsThroughDashboardGrain()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddDashboard();
            silo.UseInMemoryAdvancedReminderService();
            silo.AddAdvancedReminderDashboard();
        });
        await using var cluster = builder.Build();
        var cancellationToken = TestContext.Current.CancellationToken;
        await cluster.DeployAsync(cancellationToken);
        var table = cluster.GetSiloServiceProvider().GetRequiredService<AdvancedReminderTable>();
        var due = DateTime.UtcNow.AddDays(1);
        await table.UpsertRow(new AdvancedReminderEntry
        {
            GrainId = GrainId.Create("dashboard-test", "key"),
            ReminderName = "dashboard-integration",
            StartAt = due,
            NextDueUtc = due,
        });

        var grain = cluster.Client.GetGrain<IDashboardRemindersGrain>(0);
        var page = (await grain.GetAdvancedReminders(1, 50, cancellationToken)).Value;
        Assert.Equal(1, page.Count);
        Assert.Equal("dashboard-integration", Assert.Single(page.Reminders).Name);
    }

    [Fact]
    public async Task GetAdvancedReminders_WithoutIntegration_ReturnsEmptyPage()
    {
        var grain = new DashboardRemindersGrain(new ReminderServiceProvider(new ClassicReminderTableStub()));
        var response = (await grain.GetAdvancedReminders(1, 50, TestContext.Current.CancellationToken)).Value;

        Assert.Empty(response.Reminders);
        Assert.Equal(0, response.Count);
        Assert.False(response.HasMore);
    }

    private sealed record ReminderServiceProvider(
        ClassicReminderTable ClassicTable,
        AdvancedReminderTableStub? AdvancedTable = null,
        TimeProvider? Clock = null) : IServiceProvider
    {
        public IReminderManagementGrain? Management { get; init; }

        public object? GetService(Type serviceType)
            => serviceType == typeof(ClassicReminderTable) ? ClassicTable
                : serviceType == typeof(TimeProvider) ? Clock
                : serviceType == typeof(IAdvancedReminderDashboardSource) && AdvancedTable is { } table
                    ? new AdvancedReminderDashboardSource(table, Management ?? new PagedAdvancedReminderManagementGrain(table.Rows))
                    : null;
    }

    private sealed class ClassicReminderTableStub(params ClassicReminderEntry[] reminders) : ClassicReminderTable
    {
        public int RangeReadCount { get; private set; }

        public uint BeginHash { get; private set; }

        public uint EndHash { get; private set; }

        public Task<ClassicReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadCount++;
            BeginHash = begin;
            EndHash = end;
            return Task.FromResult(new ClassicReminderTableData(reminders));
        }

        public Task<ClassicReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ClassicReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string?> UpsertRow(ClassicReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class AdvancedReminderTableStub(params AdvancedReminderEntry[] reminders) : AdvancedReminderTable
    {
        public long StoredCount { get; set; } = reminders.Length;
        public int CountReadCount { get; private set; }

        public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountReadCount++;
            return Task.FromResult(StoredCount);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public int RangeReadCount { get; private set; }

        public AdvancedReminderEntry[] Rows { get; } = reminders;

        public Task<AdvancedReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadCount++;
            throw new NotSupportedException();
        }

        public Task<AdvancedReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
        {
            RangeReadCount++;
            throw new NotSupportedException();
        }

        public Task<AdvancedReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<AdvancedReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string> UpsertRow(AdvancedReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class CountTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class PagedAdvancedReminderManagementGrain(AdvancedReminderEntry[]? rows = null) : IReminderManagementGrain
    {
        private readonly AdvancedReminderEntry[] _rows = rows ??
        [
            new AdvancedReminderEntry { GrainId = GrainId.Create("advanced-grain", "1"), ReminderName = "advanced-reminder-1" },
            new AdvancedReminderEntry { GrainId = GrainId.Create("advanced-grain", "2"), ReminderName = "advanced-reminder-2" },
        ];

        public int ListCallCount { get; private set; }
        public int MaxPageSize { get; init; } = int.MaxValue;

        public Task<ReminderManagementPage> ListAllAsync(int pageSize = 256, string? continuationToken = null)
        {
            ListCallCount++;
            var offset = continuationToken is null ? 0 : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            var page = _rows.Skip(offset).Take(Math.Min(pageSize, MaxPageSize)).ToArray();
            var nextOffset = offset + page.Length;
            return Task.FromResult(new ReminderManagementPage
            {
                Reminders = [.. page],
                ContinuationToken = nextOffset < _rows.Length ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            });
        }

        public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();

        public Task<ReminderManagementPage> ListDueInRangeAsync(DateTime fromUtcInclusive, DateTime toUtcInclusive, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();

        public Task<ReminderManagementPage> ListFilteredAsync(ReminderQueryFilter filter, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();

        public Task<IEnumerable<AdvancedReminderEntry>> ListForGrainAsync(GrainId grainId) => throw new NotSupportedException();

        public Task SetActionAsync(GrainId grainId, string name, MissedReminderAction action) => throw new NotSupportedException();

        public Task RepairAsync(GrainId grainId, string name) => throw new NotSupportedException();

        public Task DeleteAsync(GrainId grainId, string name) => throw new NotSupportedException();
    }
}
