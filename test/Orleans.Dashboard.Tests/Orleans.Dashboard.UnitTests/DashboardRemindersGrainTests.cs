using System;
using System.Threading.Tasks;
using Orleans.Dashboard.Implementation.Grains;
using Orleans.Runtime;
using Xunit;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;
using AdvancedReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;
using ReminderPriority = Orleans.AdvancedReminders.Runtime.ReminderPriority;
using ClassicReminderEntry = Orleans.ReminderEntry;
using ClassicReminderTable = Orleans.IReminderTable;
using ClassicReminderTableData = Orleans.ReminderTableData;

namespace Orleans.Dashboard.UnitTests;

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
                    Priority = ReminderPriority.High,
                    Action = MissedReminderAction.FireImmediately,
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetReminders(1, 50)).Value;

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
                    Priority = ReminderPriority.High,
                    Action = MissedReminderAction.FireImmediately,
                }));
        var grain = new DashboardRemindersGrain(serviceProvider);

        var response = (await grain.GetAdvancedReminders(1, 50)).Value;

        Assert.Equal(1, response.Count);
        var advanced = Assert.Single(response.Reminders);
        Assert.Equal("advanced-reminder", advanced.Name);
        Assert.Equal("0 */5 * * * *", advanced.CronExpression);
        Assert.Equal("Europe/Paris", advanced.CronTimeZoneId);
        Assert.Equal(nextDue, advanced.NextDueUtc);
        Assert.Equal(lastFire, advanced.LastFireUtc);
        Assert.Equal("High", advanced.Priority);
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

        var response = (await grain.GetAdvancedReminders(1, 50)).Value;

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

        var response = (await grain.GetAdvancedReminders(2, 1)).Value;

        Assert.Equal(2, response.Count);
        var reminder = Assert.Single(response.Reminders);
        Assert.Equal("advanced-reminder-2", reminder.Name);
    }

    [Fact]
    public async Task GetAdvancedReminders_ReadsFullHashRange()
    {
        var advancedReminderTable = new AdvancedReminderTableStub();
        var serviceProvider = new ReminderServiceProvider(new ClassicReminderTableStub(), advancedReminderTable);
        var grain = new DashboardRemindersGrain(serviceProvider);

        await grain.GetAdvancedReminders(1, 50);

        Assert.Equal(1, advancedReminderTable.RangeReadCount);
        Assert.Equal(0u, advancedReminderTable.BeginHash);
        Assert.Equal(0u, advancedReminderTable.EndHash);
    }

    [Fact]
    public async Task GetReminders_ReadsFullHashRange()
    {
        var classicReminderTable = new ClassicReminderTableStub();
        var serviceProvider = new ReminderServiceProvider(classicReminderTable, new AdvancedReminderTableStub());
        var grain = new DashboardRemindersGrain(serviceProvider);

        await grain.GetReminders(1, 50);

        Assert.Equal(1, classicReminderTable.RangeReadCount);
        Assert.Equal(0u, classicReminderTable.BeginHash);
        Assert.Equal(0u, classicReminderTable.EndHash);
    }

    private sealed class ReminderServiceProvider(
        ClassicReminderTable classicReminderTable,
        AdvancedReminderTable advancedReminderTable) : IServiceProvider
    {
        public object GetService(Type serviceType)
            => serviceType == typeof(ClassicReminderTable)
                ? classicReminderTable
                : serviceType == typeof(AdvancedReminderTable)
                    ? advancedReminderTable
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

        public Task<ClassicReminderEntry> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string> UpsertRow(ClassicReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class AdvancedReminderTableStub(params AdvancedReminderEntry[] reminders) : AdvancedReminderTable
    {
        public int RangeReadCount { get; private set; }

        public uint BeginHash { get; private set; }

        public uint EndHash { get; private set; }

        public Task<AdvancedReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadCount++;
            BeginHash = begin;
            EndHash = end;
            return Task.FromResult(new AdvancedReminderTableData(reminders));
        }

        public Task<AdvancedReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<AdvancedReminderEntry> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string> UpsertRow(AdvancedReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }
}
