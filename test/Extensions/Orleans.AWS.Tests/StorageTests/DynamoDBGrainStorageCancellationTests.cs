using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Persistence")]
[TestCategory("DynamoDB"), TestCategory("BVT")]
public class DynamoDBGrainStorageCancellationTests
{
    private static readonly GrainId GrainId = GrainId.Create("test", "grain");
    private const string GrainType = "state";
    private const string TableName = "grain-state";

    public static TheoryData<string, string?, bool> Operations => new()
    {
        { "Read", "7", false },
        { "Write", null, false },
        { "Write", "7", false },
        { "Clear", null, false },
        { "Clear", "7", false },
        { "Clear", null, true },
        { "Clear", "7", true },
    };

    public static TheoryData<string, string?, bool> Mutations => new()
    {
        { "Write", null, false },
        { "Write", "7", false },
        { "Clear", null, false },
        { "Clear", "7", false },
        { "Clear", null, true },
        { "Clear", "7", true },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task OperationsForwardExactTokenAndApplyStateOnSuccess(string operation, string? etag, bool delete)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = new RequestClient();
        var serializer = new TrackingSerializer();
        var activators = new TrackingActivatorProvider();
        IGrainStorage storage = CreateStorage(client, serializer, activators, delete);
        var state = CreateState(etag);
        var original = Assert.IsType<List<int>>(state.State);

        await Execute(storage, operation, state, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(client.Tokens));
        AssertRequest(Assert.Single(client.Requests), operation, etag, delete);
        AssertSuccessfulState(state, original, operation, etag, delete);
        Assert.Equal(operation == "Write" ? 1 : 0, serializer.SerializeCount);
        Assert.Equal(operation == "Read" ? 1 : 0, serializer.DeserializeCount);
        Assert.Equal(operation == "Clear" ? 1 : 0, activators.CreateCount);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task LegacyOperationsForwardNoneAndApplyStateOnSuccess(string operation, string? etag, bool delete)
    {
        using var client = new RequestClient();
        var storage = CreateStorage(client, new TrackingSerializer(), new TrackingActivatorProvider(), delete);
        var state = CreateState(etag);
        var original = Assert.IsType<List<int>>(state.State);

        await (operation switch
        {
            "Read" => storage.ReadStateAsync(GrainType, GrainId, state),
            "Write" => storage.WriteStateAsync(GrainType, GrainId, state),
            "Clear" => storage.ClearStateAsync(GrainType, GrainId, state),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(CancellationToken.None, Assert.Single(client.Tokens));
        AssertRequest(Assert.Single(client.Requests), operation, etag, delete);
        AssertSuccessfulState(state, original, operation, etag, delete);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PreCanceledOperationsPreserveStateBeforeWork(string operation, string? etag, bool delete)
    {
        foreach (var initialized in new[] { false, true })
        {
            using var client = new RequestClient();
            var serializer = new TrackingSerializer();
            var activators = new TrackingActivatorProvider();
            var storage = CreateStorage(client, serializer, activators, delete, initialized);
            var state = CreateState(etag);
            var original = Assert.IsType<List<int>>(state.State);
            var token = new CancellationToken(canceled: true);

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Execute(storage, operation, state, token));

            Assert.Equal(token, exception.CancellationToken);
            AssertPreservedState(state, original, etag);
            Assert.Empty(client.Requests);
            Assert.Empty(client.Tokens);
            Assert.Equal(0, serializer.SerializeCount);
            Assert.Equal(0, serializer.DeserializeCount);
            Assert.Equal(0, activators.CreateCount);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task InFlightCancellationPreservesState(string operation, string? etag, bool delete)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var client = new RequestClient { BlockUntilCanceled = true };
        var serializer = new TrackingSerializer();
        var activators = new TrackingActivatorProvider();
        var storage = CreateStorage(client, serializer, activators, delete);
        var state = CreateState(etag);
        var original = Assert.IsType<List<int>>(state.State);

        var pending = Execute(storage, operation, state, cancellation.Token);
        try
        {
            Assert.Equal(cancellation.Token, Assert.Single(client.Tokens));
            AssertRequest(Assert.Single(client.Requests), operation, etag, delete);
            Assert.False(pending.IsCompleted);
            AssertPreservedState(state, original, etag);

            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(pending.IsCanceled);
            AssertPreservedState(state, original, etag);
            Assert.Equal(0, serializer.DeserializeCount);
            Assert.Equal(0, activators.CreateCount);
        }
        finally
        {
            client.Release();
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task ConditionalFailuresPreserveState(string operation, string? etag, bool delete)
    {
        using var client = new RequestClient { Failure = new ConditionalCheckFailedException("stale state") };
        var storage = CreateStorage(client, new TrackingSerializer(), new TrackingActivatorProvider(), delete);
        var state = CreateState(etag);
        var original = Assert.IsType<List<int>>(state.State);

        await Assert.ThrowsAsync<InconsistentStateException>(() =>
            Execute(storage, operation, state, TestContext.Current.CancellationToken));

        Assert.Equal(TestContext.Current.CancellationToken, Assert.Single(client.Tokens));
        AssertRequest(Assert.Single(client.Requests), operation, etag, delete);
        AssertPreservedState(state, original, etag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadMissingOrClearedRecordResetsState(bool clearedRecord)
    {
        using var client = new RequestClient
        {
            ReadResponse = clearedRecord
                ? new GetItemResponse
                {
                    Item = new()
                    {
                        ["GrainReference"] = new AttributeValue("service_test/grain"),
                        ["GrainType"] = new AttributeValue(GrainType),
                        ["ETag"] = new AttributeValue { N = "8" },
                        ["GrainState"] = new AttributeValue { NULL = true },
                    }
                }
                : new GetItemResponse()
        };
        var serializer = new TrackingSerializer();
        var activators = new TrackingActivatorProvider();
        var storage = CreateStorage(client, serializer, activators, delete: false);
        var state = CreateState("7");

        await storage.ReadStateAsync(GrainType, GrainId, state, TestContext.Current.CancellationToken);

        Assert.Equal(TestContext.Current.CancellationToken, Assert.Single(client.Tokens));
        Assert.Empty(Assert.IsType<List<int>>(state.State));
        Assert.False(state.RecordExists);
        Assert.Equal(clearedRecord ? "8" : null, state.ETag);
        Assert.Equal(0, serializer.DeserializeCount);
        Assert.Equal(1, activators.CreateCount);
    }

    private static DynamoDBGrainStorage CreateStorage(
        RequestClient client, TrackingSerializer serializer, TrackingActivatorProvider activators, bool delete, bool initialized = true)
    {
        var provider = new DynamoDBGrainStorage("test", new DynamoDBStorageOptions
        {
            ServiceId = "service",
            TableName = TableName,
            DeleteStateOnClear = delete,
            GrainStorageSerializer = serializer,
        }, activators, NullLogger<DynamoDBGrainStorage>.Instance);
        if (initialized)
        {
            var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
            var clientField = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
            clientField.SetValue(storage, client);
            typeof(DynamoDBGrainStorage).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, storage);
        }

        return provider;
    }

    private static GrainState<List<int>> CreateState(string? etag) => new()
    {
        State = [42],
        ETag = etag,
        RecordExists = true,
    };

    private static Task Execute(IGrainStorage storage, string operation, GrainState<List<int>> state, CancellationToken token)
        => operation switch
        {
            "Read" => storage.ReadStateAsync(GrainType, GrainId, state, token),
            "Write" => storage.WriteStateAsync(GrainType, GrainId, state, token),
            "Clear" => storage.ClearStateAsync(GrainType, GrainId, state, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static void AssertPreservedState(GrainState<List<int>> state, List<int> original, string? etag)
    {
        Assert.Same(original, state.State);
        Assert.Equal(new[] { 42 }, state.State);
        Assert.Equal(etag, state.ETag);
        Assert.True(state.RecordExists);
    }

    private static void AssertSuccessfulState(GrainState<List<int>> state, List<int> original, string operation, string? etag, bool delete)
    {
        if (operation == "Read")
        {
            Assert.Equal(new[] { 99 }, state.State);
            Assert.Equal("8", state.ETag);
            Assert.True(state.RecordExists);
        }
        else if (operation == "Write")
        {
            Assert.Same(original, state.State);
            Assert.Equal(etag is null ? "0" : "8", state.ETag);
            Assert.True(state.RecordExists);
        }
        else
        {
            Assert.NotSame(original, state.State);
            Assert.Empty(Assert.IsType<List<int>>(state.State));
            Assert.Equal(delete ? null : etag is null ? "1" : "8", state.ETag);
            Assert.False(state.RecordExists);
        }
    }

    private static void AssertRequest(AmazonWebServiceRequest request, string operation, string? etag, bool delete)
    {
        const string insertCondition = "attribute_not_exists(GrainReference) AND attribute_not_exists(GrainType)";
        switch (request)
        {
            case GetItemRequest read:
                Assert.Equal("Read", operation);
                Assert.Equal(TableName, read.TableName);
                Assert.True(read.ConsistentRead);
                AssertKeys(read.Key);
                break;
            case DeleteItemRequest removal:
                Assert.Equal("Clear", operation);
                Assert.True(delete);
                Assert.Equal(TableName, removal.TableName);
                AssertKeys(removal.Key);
                Assert.Equal(etag is null ? "attribute_not_exists(ETag)" : "ETag = :currentETag", removal.ConditionExpression);
                if (etag is not null)
                {
                    Assert.Equal(etag, removal.ExpressionAttributeValues[":currentETag"].N);
                }
                break;
            case PutItemRequest insert:
                Assert.NotEqual("Read", operation);
                Assert.False(delete);
                Assert.Null(etag);
                Assert.Equal(TableName, insert.TableName);
                Assert.Equal(insertCondition, insert.ConditionExpression);
                Assert.Equal("service_test/grain", insert.Item["GrainReference"].S);
                Assert.Equal(GrainType, insert.Item["GrainType"].S);
                Assert.Equal(operation == "Write" ? "0" : "1", insert.Item["ETag"].N);
                AssertPayload(insert.Item["GrainState"], operation);
                break;
            case UpdateItemRequest update:
                Assert.NotEqual("Read", operation);
                Assert.False(delete);
                Assert.NotNull(etag);
                Assert.Equal(TableName, update.TableName);
                AssertKeys(update.Key);
                Assert.Equal("ETag = :currentETag", update.ConditionExpression);
                Assert.Equal(etag, update.ExpressionAttributeValues[":currentETag"].N);
                Assert.Equal((int.Parse(etag, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture),
                    update.ExpressionAttributeValues[":ETag"].N);
                AssertPayload(update.ExpressionAttributeValues[":GrainState"], operation);
                break;
            default:
                Assert.Fail($"Unexpected SDK request: {request.GetType()}");
                break;
        }
    }

    private static void AssertKeys(Dictionary<string, AttributeValue> keys)
    {
        Assert.Equal(2, keys.Count);
        Assert.Equal("service_test/grain", keys["GrainReference"].S);
        Assert.Equal(GrainType, keys["GrainType"].S);
    }

    private static void AssertPayload(AttributeValue value, string operation)
    {
        if (operation == "Write")
        {
            Assert.Equal(new[] { 42 }, JsonSerializer.Deserialize<List<int>>(value.B.ToArray()));
        }
        else
        {
            Assert.True(value.NULL);
            Assert.Null(value.B);
        }
    }

    private sealed class TrackingSerializer : IGrainStorageSerializer
    {
        public int SerializeCount { get; private set; }
        public int DeserializeCount { get; private set; }

        public BinaryData Serialize<T>(T? input)
        {
            SerializeCount++;
            return new BinaryData(JsonSerializer.SerializeToUtf8Bytes(input));
        }

        public T? Deserialize<T>(BinaryData input)
        {
            DeserializeCount++;
            return JsonSerializer.Deserialize<T>(input.ToMemory().Span);
        }
    }

    private sealed class TrackingActivatorProvider : IActivatorProvider
    {
        public int CreateCount { get; private set; }

        public IActivator<T> GetActivator<T>() => new TrackingActivator<T>(this);

        private sealed class TrackingActivator<T>(TrackingActivatorProvider provider) : IActivator<T>
        {
            public T Create()
            {
                provider.CreateCount++;
                return Activator.CreateInstance<T>();
            }
        }
    }

    private sealed class RequestClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockUntilCanceled { get; init; }
        public Exception? Failure { get; init; }
        public List<AmazonWebServiceRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public GetItemResponse ReadResponse { get; init; } = new()
        {
            Item = new()
            {
                ["GrainReference"] = new AttributeValue("service_test/grain"),
                ["GrainType"] = new AttributeValue(GrainType),
                ["ETag"] = new AttributeValue { N = "8" },
                ["GrainState"] = new AttributeValue { B = new MemoryStream("[99]"u8.ToArray()) },
            }
        };

        public void Release() => _completion.TrySetResult();

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
            => Execute(request, cancellationToken, ReadResponse);

        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
            => Execute(request, cancellationToken, new PutItemResponse());

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
            => Execute(request, cancellationToken, new UpdateItemResponse { Attributes = [] });

        public override Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
            => Execute(request, cancellationToken, new DeleteItemResponse());

        private async Task<T> Execute<T>(AmazonWebServiceRequest request, CancellationToken token, T response)
        {
            Requests.Add(request);
            Tokens.Add(token);
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (BlockUntilCanceled)
            {
                using var registration = token.Register(() => _completion.TrySetCanceled(token));
                await _completion.Task;
            }

            return response;
        }
    }
}
