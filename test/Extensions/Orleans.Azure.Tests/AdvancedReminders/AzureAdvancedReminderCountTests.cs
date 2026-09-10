#nullable enable
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.AdvancedReminders.AzureStorage;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class AzureAdvancedReminderCountTests
{
    [Fact]
    public async Task Count_ReadsProjectedKeysForOneServiceAcrossAllPages()
    {
        var client = Substitute.For<TableClient>();
        var response = Substitute.For<Response>();
        var rows = Enumerable.Range(0, 1_000).Select(static _ => new TableEntity()).ToArray();
        var pages = AsyncPageable<TableEntity>.FromPages([
            Page<TableEntity>.FromValues(rows, "next", response),
            Page<TableEntity>.FromValues([], "empty-next", response),
            Page<TableEntity>.FromValues([new TableEntity()], null, response),
        ]);
        client.QueryAsync<TableEntity>(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(pages);
        var options = new AzureTableReminderStorageOptions
        {
            TableServiceClient = new TableServiceClient("UseDevelopmentStorage=true"),
        };
        var manager = new RemindersTableManager("service", "cluster", options, NullLoggerFactory.Instance);
        typeof(RemindersTableManager).BaseType!.GetProperty("Table")!.SetValue(manager, client);

        Assert.Equal(1_001, await manager.GetCountAsync(TestContext.Current.CancellationToken));
        var call = Assert.Single(client.ReceivedCalls());
        var args = call.GetArguments();
        var (lower, upper) = ReminderTableEntry.ConstructPartitionKeyBounds("service");
        Assert.Equal(TableClient.CreateQueryFilter($"(PartitionKey gt {lower}) and (PartitionKey lt {upper})"), args[0]);
        Assert.Equal(1_000, args[1]);
        Assert.Equal(["PartitionKey"], Assert.IsAssignableFrom<IEnumerable<string>>(args[2]));
        Assert.Equal(TestContext.Current.CancellationToken, args[3]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetCountAsync(new CancellationToken(canceled: true)));
        Assert.Single(client.ReceivedCalls());
    }
}
