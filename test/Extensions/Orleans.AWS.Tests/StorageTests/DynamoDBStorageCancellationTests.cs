using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
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

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task QueryAsyncForwardsCancellationAndPageOptions(int? limit)
    {
        using var client = new RecordingQueryClient();
        var storage = CreateStorage(client);
        var keys = new Dictionary<string, AttributeValue> { [":id"] = new() { S = "partition" } };
        var continuation = new Dictionary<string, AttributeValue> { ["id"] = new() { S = "previous" } };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var page = limit.HasValue
            ? await storage.QueryAsync("TestTable", keys, "id = :id", item => item["value"].S,
                "by-id", false, continuation, false, limit, cancellation.Token)
            : await storage.QueryAsync("TestTable", keys, "id = :id", item => item["value"].S,
                cancellation.Token, "by-id", false, continuation, false);

        Assert.Equal(1, client.QueryCount);
        Assert.Equal(cancellation.Token, client.Token);
        var request = Assert.IsType<QueryRequest>(client.Request);
        Assert.Equal("TestTable", request.TableName);
        Assert.Same(keys, request.ExpressionAttributeValues);
        Assert.Equal("id = :id", request.KeyConditionExpression);
        Assert.Equal("by-id", request.IndexName);
        Assert.False(request.ScanIndexForward);
        Assert.False(request.ConsistentRead);
        Assert.Equal(limit, request.Limit);
        Assert.Same(continuation, request.ExclusiveStartKey);
        Assert.Equal(["resolved-row"], page.results);
        Assert.Same(client.Response.LastEvaluatedKey, page.lastEvaluatedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task QueryAsyncHonorsCancellationBeforeWork(int? limit)
    {
        using var client = new RecordingQueryClient();
        var storage = CreateStorage(client);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limit.HasValue
            ? storage.QueryAsync("TestTable", [], "id = :id", item => item["value"].S,
                "", true, null, true, limit, cancellation.Token)
            : storage.QueryAsync("TestTable", [], "id = :id", item => item["value"].S, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, client.QueryCount);
        Assert.Null(client.Request);
    }

    private static DynamoDBStorage CreateStorage(RecordingQueryClient client)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
        var clientField = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
        clientField.SetValue(storage, client);
        return storage;
    }

    private sealed class RecordingQueryClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public QueryRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public int QueryCount { get; private set; }
        public QueryResponse Response { get; } = new()
        {
            Items = [new() { ["value"] = new() { S = "resolved-row" } }],
            LastEvaluatedKey = new() { ["id"] = new() { S = "next" } },
        };

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Token = cancellationToken;
            QueryCount++;
            return Task.FromResult(Response);
        }
    }
}
