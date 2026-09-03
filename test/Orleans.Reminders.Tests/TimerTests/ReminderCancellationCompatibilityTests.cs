using System.Reflection;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

public class ReminderCancellationCompatibilityTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public async Task IRemindable_CancellationOverload_ForwardsToLegacyImplementation()
    {
        IRemindable remindable = new LegacyRemindable();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var status = new TickStatus(DateTime.UtcNow, TimeSpan.FromMinutes(1), DateTime.UtcNow);

        await remindable.ReceiveReminder("test", status, cancellation.Token);

        var received = Assert.IsType<(string Name, TickStatus Status)>(((LegacyRemindable)remindable).Received);
        Assert.Equal("test", received.Name);
        Assert.Equal(status, received.Status);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public void IRemindable_Overloads_PreserveWireIdentity()
    {
        var legacyMethod = typeof(IRemindable).GetMethod(
            nameof(IRemindable.ReceiveReminder),
            [typeof(string), typeof(TickStatus)])!;
        var cancellationMethod = typeof(IRemindable).GetMethod(
            nameof(IRemindable.ReceiveReminder),
            [typeof(string), typeof(TickStatus), typeof(CancellationToken)])!;

        Assert.Equal("ReceiveReminder", legacyMethod.GetCustomAttribute<AliasAttribute>()?.Alias);
        Assert.Equal("6461BF2F", cancellationMethod.GetCustomAttribute<AliasAttribute>()?.Alias);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public async Task IReminderTable_CancellationOverload_ForwardsToLegacyImplementation()
    {
        IReminderTable table = new LegacyReminderTable();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await table.TestOnlyClearTable(cancellation.Token);

        Assert.True(((LegacyReminderTable)table).ClearCalled);
    }

    private sealed class LegacyRemindable : IRemindable
    {
        public (string Name, TickStatus Status)? Received { get; private set; }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            Received = (reminderName, status);
            return Task.CompletedTask;
        }
    }

    private sealed class LegacyReminderTable : IReminderTable
    {
        public bool ClearCalled { get; private set; }

        public Task Init() => Task.CompletedTask;
        public Task<ReminderTableData> ReadRows(GrainId grainId) => Task.FromResult(new ReminderTableData());
        public Task<ReminderTableData> ReadRows(uint begin, uint end) => Task.FromResult(new ReminderTableData());
        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => Task.FromResult<ReminderEntry?>(null);
        public Task<string?> UpsertRow(ReminderEntry entry) => Task.FromResult<string?>(null);
        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => Task.FromResult(false);

        public Task TestOnlyClearTable()
        {
            ClearCalled = true;
            return Task.CompletedTask;
        }
    }
}
