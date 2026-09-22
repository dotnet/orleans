using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MembershipStorage = Orleans.Clustering.DynamoDB.DynamoDBStorage;
using ReminderStorage = Orleans.Reminders.DynamoDB.DynamoDBStorage;

namespace AWSUtils.Tests.StorageTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestCategory("DynamoDB"), TestCategory("BVT")]
public class DynamoDBBatchDeleteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialBatchResponseSurfacesFailure(bool reminders)
    {
        using var client = new BatchClient
        {
            Response = new BatchWriteItemResponse
            {
                UnprocessedItems = new()
                {
                    ["table"] = [new WriteRequest { DeleteRequest = new DeleteRequest { Key = CreateKey() } }]
                }
            }
        };
        var delete = CreateDelete(reminders, client, [CreateKey()]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => delete(TestContext.Current.CancellationToken));

        Assert.Equal("Amazon DynamoDB failed to delete 1 item(s) from table table.", exception.Message);
        var request = Assert.Single(client.Requests);
        Assert.Single(request.RequestItems);
        var item = Assert.Single(request.RequestItems["table"]);
        Assert.Null(item.PutRequest);
        Assert.Equal("item", item.DeleteRequest.Key["id"].S);
        Assert.Equal(TestContext.Current.CancellationToken, Assert.Single(client.Tokens));
    }

    [Theory]
    [InlineData(false, "omitted")]
    [InlineData(true, "omitted")]
    [InlineData(false, "empty-map")]
    [InlineData(true, "empty-map")]
    [InlineData(false, "empty-items")]
    [InlineData(true, "empty-items")]
    public async Task CompleteBatchResponseSucceeds(bool reminders, string responseShape)
    {
        using var client = new BatchClient
        {
            Response = new BatchWriteItemResponse
            {
                UnprocessedItems = responseShape switch
                {
                    "omitted" => null,
                    "empty-map" => [],
                    "empty-items" => new() { ["table"] = [] },
                    _ => throw new ArgumentOutOfRangeException(nameof(responseShape))
                }
            }
        };

        await CreateDelete(reminders, client, [CreateKey()])(TestContext.Current.CancellationToken);

        Assert.Single(client.Requests);
        Assert.Equal(TestContext.Current.CancellationToken, Assert.Single(client.Tokens));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledAndEmptyBatchesAvoidNativeRequests(bool reminders)
    {
        using var client = new BatchClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var delete = CreateDelete(reminders, client, [CreateKey()]);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await CreateDelete(reminders, client, [])(TestContext.Current.CancellationToken);

        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBatchFailurePropagates(bool reminders)
    {
        var failure = new AmazonDynamoDBException("Native failure.");
        using var client = new BatchClient { Failure = failure };

        var exception = await Assert.ThrowsAsync<AmazonDynamoDBException>(() =>
            CreateDelete(reminders, client, [CreateKey()])(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Single(client.Requests);
        Assert.Equal(TestContext.Current.CancellationToken, Assert.Single(client.Tokens));
    }

    private static Dictionary<string, AttributeValue> CreateKey() => new() { ["id"] = new AttributeValue("item") };

    private static Func<CancellationToken, Task> CreateDelete(
        bool reminders, AmazonDynamoDBClient client, IReadOnlyCollection<Dictionary<string, AttributeValue>> keys)
    {
        if (reminders)
        {
            var storage = new ReminderStorage(NullLogger<ReminderStorage>.Instance, "http://localhost");
            InstallClient(storage, client);
            return token => storage.DeleteEntriesAsync("table", keys, token);
        }
        else
        {
            var storage = new MembershipStorage(NullLogger<MembershipStorage>.Instance, "http://localhost");
            InstallClient(storage, client);
            return token => storage.DeleteEntriesAsync("table", keys, token);
        }
    }

    private static void InstallClient(object storage, AmazonDynamoDBClient client)
    {
        var field = storage.GetType().GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsAssignableFrom<IDisposable>(field.GetValue(storage)).Dispose();
        field.SetValue(storage, client);
    }

    private sealed class BatchClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public BatchWriteItemResponse Response { get; init; } = new();
        public AmazonDynamoDBException? Failure { get; init; }
        public List<BatchWriteItemRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            return Failure is { } failure ? Task.FromException<BatchWriteItemResponse>(failure) : Task.FromResult(Response);
        }
    }
}
