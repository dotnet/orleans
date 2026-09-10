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
    public async Task QueryCountAsync_SumsAllPagesWithoutRequestingItems()
    {
        var continuation = new Dictionary<string, AttributeValue> { ["id"] = new() { S = "next" } };
        using var client = new RecordingQueryClient
        {
            CountResponses = new Queue<QueryResponse>([
                new() { Count = 0, LastEvaluatedKey = continuation },
                new() { Count = int.MaxValue, LastEvaluatedKey = continuation },
                new() { Count = 2 },
            ]),
        };
        var storage = CreateStorage(client);
        var keys = new Dictionary<string, AttributeValue> { [":ServiceId"] = new() { S = "service" } };

        var count = await storage.QueryCountAsync("reminders", keys, "ServiceId = :ServiceId", "ServiceIdIndex", TestContext.Current.CancellationToken);

        Assert.Equal((long)int.MaxValue + 2, count);
        Assert.Equal(3, client.QueryCount);
        Assert.Null(client.CountContinuationKeys[0]);
        Assert.Same(continuation, client.CountContinuationKeys[1]);
        Assert.Same(continuation, client.CountContinuationKeys[2]);
        Assert.Equal(TestContext.Current.CancellationToken, client.Token);
        var request = Assert.IsType<QueryRequest>(client.Request);
        Assert.Equal(Select.COUNT, request.Select);
        Assert.Equal("reminders", request.TableName);
        Assert.Equal("ServiceId = :ServiceId", request.KeyConditionExpression);
        Assert.Same(keys, request.ExpressionAttributeValues);
        Assert.Equal("ServiceIdIndex", request.IndexName);
        Assert.False(request.ConsistentRead);
    }

    [Fact]
    public async Task QueryCountAsync_HonorsCancellationBeforeWork()
    {
        using var client = new RecordingQueryClient();
        var storage = CreateStorage(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.QueryCountAsync(
            "reminders", [], "ServiceId = :ServiceId", "ServiceIdIndex", new CancellationToken(canceled: true)));

        Assert.Equal(0, client.QueryCount);
    }

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
        public Queue<QueryResponse>? CountResponses { get; init; }
        public List<Dictionary<string, AttributeValue>?> CountContinuationKeys { get; } = [];
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
            if (CountResponses is not null)
            {
                CountContinuationKeys.Add(request.ExclusiveStartKey);
                return Task.FromResult(CountResponses.Dequeue());
            }

            return Task.FromResult(Response);
        }
    }
}
