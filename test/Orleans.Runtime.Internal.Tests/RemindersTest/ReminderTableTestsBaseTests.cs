using Orleans.Reminders.TestKit;
using Xunit;

namespace UnitTests.RemindersTest;

public class ReminderTableTestsBaseTests
{
    [Fact]
    public async Task RemindersRange_UsesRequestedIterationCount()
    {
        const int RequestedCount = 7;
        var table = new IdealizedReminderTable(nameof(RemindersRange_UsesRequestedIterationCount));
        var runner = new IdealizedRunner(table);

        await ReminderTableTestsBase.RunRemindersRange(runner, RequestedCount);

        var requestedUpserts = table.Operations
            .Where(operation =>
                operation.Kind == ReminderTableOperationKind.UpsertRow
                && operation.ReminderName is not null
                && operation.ReminderName.StartsWith("requested-cardinality-", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(RequestedCount, requestedUpserts.Count);
        Assert.Equal(
            RequestedCount,
            requestedUpserts.Select(operation => (operation.GrainId, operation.ReminderName)).Distinct().Count());
        Assert.All(requestedUpserts, operation => Assert.False(string.IsNullOrEmpty(operation.ResultETag)));

        Assert.Contains(
            table.Operations,
            operation => operation.Kind == ReminderTableOperationKind.ReadRange
                && operation.Begin == 0
                && operation.End == 0
                && operation.ResultCount == RequestedCount);
        Assert.Empty(table.Snapshot());
    }

    private sealed class IdealizedRunner(IdealizedReminderTable table)
        : ReminderTableTestRunner(table, nameof(IdealizedRunner));
}
