#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderIteratorTests
{
    [Fact]
    public async Task EnumerateAllAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListAllAsync(2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1"), CreateReminder("r2")],
            ContinuationToken = "next",
        }));
        managementGrain.ListAllAsync(2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r3")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateAllAsync(pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2", "r3"], names);
    }

    [Fact]
    public async Task EnumerateOverdueAsync_ReadsAllPages()
    {
        var overdue = TimeSpan.FromMinutes(2);
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListOverdueAsync(overdue, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListOverdueAsync(overdue, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateOverdueAsync(overdue, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateDueInRangeAsync_ReadsAllPages()
    {
        var from = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListDueInRangeAsync(from, to, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListDueInRangeAsync(from, to, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateDueInRangeAsync(from, to, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateFilteredAsync_ReadsAllPages()
    {
        var filter = new ReminderQueryFilter
        {
            Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
            OverdueBy = TimeSpan.FromMinutes(2),
            MissedBy = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Critical,
        };

        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListFilteredAsync(filter, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListFilteredAsync(filter, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateFilteredAsync(filter, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public void Ctor_NullManagementGrain_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new ReminderIterator(null!));
    }

    private static ReminderEntry CreateReminder(string reminderName)
        => new()
        {
            GrainId = GrainId.Create("test", reminderName),
            ReminderName = reminderName,
            StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(1),
        };
}
