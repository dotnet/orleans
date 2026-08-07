using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

[TestCategory("DynamoDB"), TestCategory("BVT")]
public class DynamoDBStorageCancellationTests
{
    [Fact]
    public async Task InitializeTableHonorsCancellationBeforeWork()
    {
        var storage = new DynamoDBStorage(
            NullLogger<DynamoDBStorage>.Instance,
            service: "http://localhost",
            createIfNotExists: false,
            updateIfExists: false);
        var cancellationToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            storage.InitializeTable("TestTable", [], [], cancellationToken: cancellationToken));
    }
}
