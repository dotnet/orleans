#nullable enable
using NSubstitute;
using Orleans.AdvancedReminders;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderManagementGrainExtensionsTests
{
    [Fact]
    public void GetReminderManagementGrain_UsesWellKnownSingletonKey()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var expected = Substitute.For<IReminderManagementGrain>();
        grainFactory.GetGrain<IReminderManagementGrain>(ReminderManagementGrainExtensions.GrainKey).Returns(expected);

        var result = grainFactory.GetReminderManagementGrain();

        Assert.Same(expected, result);
        grainFactory.Received(1).GetGrain<IReminderManagementGrain>(ReminderManagementGrainExtensions.GrainKey);
    }

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

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateAllAsync(
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2", "r3"], names);
        await managementGrain.Received(1).ListAllAsync(2, null);
        await managementGrain.Received(1).ListAllAsync(2, "next");
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
    public async Task EnumerateOverdueAsync_Extension_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(2), 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(2), 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateOverdueAsync(
            TimeSpan.FromMinutes(2),
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateDueInRangeAsync_Extension_ReadsAllPages()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
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

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateDueInRangeAsync(
            from,
            to,
            pageSize: 2,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
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
