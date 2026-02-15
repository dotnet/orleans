#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderManagementGrainExtensionsTests
{
    [Fact]
    public async Task EnumerateAllAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListAllAsync(2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r1"),
                CreateReminder("r2"),
            ],
            ContinuationToken = "next",
        }));
        managementGrain.ListAllAsync(2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r3"),
            ],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateAllAsync(pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2", "r3"], names);
        await managementGrain.Received(1).ListAllAsync(2, null);
        await managementGrain.Received(1).ListAllAsync(2, "next");
    }

    [Fact]
    public async Task EnumerateOverdueAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(5), 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r1"),
            ],
            ContinuationToken = "next",
        }));
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(5), 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r2"),
            ],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateOverdueAsync(TimeSpan.FromMinutes(5), pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
        await managementGrain.Received(1).ListOverdueAsync(TimeSpan.FromMinutes(5), 2, null);
        await managementGrain.Received(1).ListOverdueAsync(TimeSpan.FromMinutes(5), 2, "next");
    }

    [Fact]
    public async Task EnumerateDueInRangeAsync_ReadsAllPages()
    {
        var from = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListDueInRangeAsync(from, to, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r1"),
            ],
            ContinuationToken = "next",
        }));
        managementGrain.ListDueInRangeAsync(from, to, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r2"),
            ],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateDueInRangeAsync(from, to, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
        await managementGrain.Received(1).ListDueInRangeAsync(from, to, 2, null);
        await managementGrain.Received(1).ListDueInRangeAsync(from, to, 2, "next");
    }

    [Fact]
    public async Task EnumerateFilteredAsync_ReadsAllPages()
    {
        var filter = new ReminderQueryFilter
        {
            Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
            OverdueBy = TimeSpan.FromMinutes(2),
            MissedBy = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.High,
        };

        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListFilteredAsync(filter, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r1"),
            ],
            ContinuationToken = "next",
        }));
        managementGrain.ListFilteredAsync(filter, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders =
            [
                CreateReminder("r2"),
            ],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateFilteredAsync(filter, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
        await managementGrain.Received(1).ListFilteredAsync(filter, 2, null);
        await managementGrain.Received(1).ListFilteredAsync(filter, 2, "next");
    }

    [Fact]
    public void CreateIterator_ReturnsIteratorFacade()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();

        var iterator = managementGrain.CreateIterator();

        Assert.NotNull(iterator);
        Assert.IsType<ReminderIterator>(iterator);
    }

    [Fact]
    public async Task EnumerateAllAsync_NullGrain_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await using var enumerator = ReminderManagementGrainExtensions.EnumerateAllAsync(null!).GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });
    }

    [Fact]
    public void CreateIterator_NullGrain_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReminderManagementGrainExtensions.CreateIterator(null!));
    }

    private static ReminderEntry CreateReminder(string reminderName)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Create("test", reminderName),
            ReminderName = reminderName,
            StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(1),
        };
    }
}
