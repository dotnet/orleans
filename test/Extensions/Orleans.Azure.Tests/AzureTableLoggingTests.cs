using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Orleans.Clustering.AzureStorage;
using Xunit;

namespace Tester.AzureUtils;

public sealed class AzureTableLoggingTests
{
    [Fact]
    public async Task SlowAccessWarningPreservesTableNameAndOperation()
    {
        var logger = new FakeLogger();
        var options = new AzureStorageOperationOptions { TableName = "LoggingTestTable" };
        // Force the warning without waiting or contacting Azure.
        options.StoragePolicyOptions.OperationTimeout = TimeSpan.FromTicks(1);
        options.TableServiceClient = new FailingTableServiceClient();
        var manager = new AzureTableDataManager<TableEntity>(options, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(manager.DeleteTableAsync);

        var warning = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
        Assert.Equal("LoggingTestTable", warning.GetStructuredStateValue("TableName"));
        Assert.Equal("DeleteTable", warning.GetStructuredStateValue("Operation"));
    }

    private sealed class FailingTableServiceClient : TableServiceClient
    {
        public override Task<Azure.Response> DeleteTableAsync(string tableName, CancellationToken cancellationToken = default)
            => Task.FromException<Azure.Response>(new InvalidOperationException("Delete failed"));
    }
}
