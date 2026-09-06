using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB;
using Orleans.Transactions.DynamoDB.TransactionalState;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.Base.FaultInjection.ControlledInjection;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class FaultInjectionDynamoDBTransactionStateStorageTests
{
    private const string ProviderName = "dynamodb-faults";
    private const string TableName = "Transactions";

    [Fact]
    public async Task Store_NullMetadata_ThrowsWithMetadataParamNameBeforeInjectorOrStorage()
    {
        var events = new List<string>();
        using var client = new RecordingAmazonDynamoDBClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(client, injector);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.Store(
                expectedETag: "unobserved-etag",
                metadata: null!,
                statesToPrepare: null,
                commitUpTo: null,
                abortAfter: null));

        Assert.Equal("metadata", exception.ParamName);
        Assert.Empty(events);
        Assert.Empty(client.GetItemCalls);
        Assert.Empty(client.QueryCalls);
        Assert.Empty(client.TransactWriteItemsCalls);
    }

    [Fact]
    public async Task Load_ForwardsFreshSnapshotWithoutInvokingInjector()
    {
        var events = new List<string>();
        using var client = new RecordingAmazonDynamoDBClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(client, injector);

        var result = await storage.Load();

        Assert.Null(result.ETag);
        Assert.Equal(0, result.CommittedSequenceId);
        Assert.NotNull(result.CommittedState);
        Assert.Empty(result.Metadata.CommitRecords);
        Assert.Empty(result.PendingStates);
        Assert.Equal(2, client.GetItemCalls.Count);
        Assert.Single(client.QueryCalls);
        Assert.Empty(events);
        Assert.Empty(injector.BeforeTransactionIds);
        Assert.Empty(injector.AfterTransactionIds);
    }

    [Fact]
    public async Task Store_ValidMetadataAndETag_ForwardsStorageCallBetweenInjectorCallbacks()
    {
        var events = new List<string>();
        using var client = new RecordingAmazonDynamoDBClient(events);
        var injector = new RecordingFaultInjector(events);
        var storage = CreateStorage(client, injector);
        var firstTransactionId = new Guid("89DD96F6-9BDD-4637-96AA-12AB6B5EF701");
        var secondTransactionId = new Guid("067485E1-35D1-453F-9B64-DBCC87BCBD8E");

        var loaded = await storage.Load();
        var firstMetadata = CreateMetadata(firstTransactionId, 303);
        var firstETag = await storage.Store(
            loaded.ETag,
            firstMetadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);
        var secondMetadata = CreateMetadata(secondTransactionId, 404);
        var secondETag = await storage.Store(
            firstETag,
            secondMetadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);

        Assert.Equal("0", firstETag);
        Assert.Equal("1", secondETag);
        Assert.Equal(
            ["before", "storage", "after", "before", "storage", "after"],
            events);
        Assert.Collection(
            injector.BeforeTransactionIds,
            ids => Assert.Equal([firstTransactionId], ids),
            ids => Assert.Equal([secondTransactionId], ids));
        Assert.Collection(
            injector.AfterTransactionIds,
            ids => Assert.Equal([firstTransactionId], ids),
            ids => Assert.Equal([secondTransactionId], ids));

        Assert.Equal(2, client.TransactWriteItemsCalls.Count);
        var firstKeyPut = Assert.Single(client.TransactWriteItemsCalls[0].TransactItems).Put;
        Assert.Equal(TableName, firstKeyPut.TableName);
        Assert.Equal("key", firstKeyPut.Item[DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME].S);
        Assert.Equal("0", firstKeyPut.Item[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);
        Assert.NotEmpty(firstKeyPut.Item["Metadata"].B.ToArray());
        Assert.Contains("attribute_not_exists", firstKeyPut.ConditionExpression);
        AssertMetadataEqual(
            firstMetadata,
            DeserializeMetadata(firstKeyPut.Item["Metadata"].B.ToArray()));

        var secondKeyPut = Assert.Single(client.TransactWriteItemsCalls[1].TransactItems).Put;
        Assert.Equal("1", secondKeyPut.Item[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);
        Assert.Equal(
            "0",
            secondKeyPut.ExpressionAttributeValues[
                DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS].N);
        Assert.NotEmpty(secondKeyPut.Item["Metadata"].B.ToArray());
        AssertMetadataEqual(
            secondMetadata,
            DeserializeMetadata(secondKeyPut.Item["Metadata"].B.ToArray()));
    }

    [Fact]
    public void FactoryCreate_RegisteredNamedOptions_ReturnsWrapperFactoryWithoutStorageAccess()
    {
        using var services = CreateServices();

        var result = FaultInjectionDynamoDBTransactionStateStorageFactory.Create(
            services,
            ProviderName);

        Assert.IsType<FaultInjectionDynamoDBTransactionStateStorageFactory>(result);
    }

    [Fact]
    public async Task Participate_CanceledStart_ForwardsCancellationTokenWithoutContactingStorage()
    {
        var options = CreateOptions();
        var inner = new DynamoDBTransactionalStateStorageFactory(
            ProviderName,
            options,
            Options.Create(new ClusterOptions { ServiceId = "service" }),
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance);
        var factory = new FaultInjectionDynamoDBTransactionStateStorageFactory(inner);
        var lifecycle = new RecordingSiloLifecycle();
        factory.Participate(lifecycle);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.StartAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(DynamoDBTransactionalStorageOptions.DEFAULT_INIT_STAGE, lifecycle.Stage);
    }

    private static FaultInjectionDynamoDBTransactionStateStorage<TestState> CreateStorage(
        RecordingAmazonDynamoDBClient client,
        ITransactionFaultInjector injector)
    {
        var storage = new DynamoDBStorage(
            NullLogger.Instance,
            "http://127.0.0.1:65535",
            accessKey: "dummy",
            secretKey: "dummy");
        typeof(DynamoDBStorage)
            .GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(storage, client);
        var inner = new DynamoDBTransactionalStateStorage<TestState>(
            storage,
            CreateOptions(),
            "test-partition",
            NullLogger<DynamoDBTransactionalStateStorage<TestState>>.Instance);
        return new FaultInjectionDynamoDBTransactionStateStorage<TestState>(
            injector,
            inner);
    }

    private static DynamoDBTransactionalStorageOptions CreateOptions() => new()
    {
        Service = "http://127.0.0.1:65535",
        AccessKey = "dummy",
        SecretKey = "dummy",
        TableName = TableName,
        CreateIfNotExists = false,
        UpdateIfExists = false,
        GrainStorageSerializer = new JsonGrainStorageSerializer(
            new OrleansJsonSerializer(
                new OptionsWrapper<OrleansJsonSerializerOptions>(
                    new OrleansJsonSerializerOptions()))),
    };

    private static TransactionalStateMetaData CreateMetadata(Guid transactionId, long ticks) => new()
    {
        TimeStamp = new DateTime(ticks),
        CommitRecords =
        {
            [transactionId] = new CommitRecord
            {
                Timestamp = new DateTime(ticks + 1),
                WriteParticipants = [],
            },
        },
    };

    private static TransactionalStateMetaData DeserializeMetadata(byte[] value) =>
        CreateOptions().GrainStorageSerializer.Deserialize<TransactionalStateMetaData>(
            new BinaryData(value))!;

    private static void AssertMetadataEqual(
        TransactionalStateMetaData expected,
        TransactionalStateMetaData actual)
    {
        Assert.Equal(expected.TimeStamp, actual.TimeStamp);
        var expectedRecord = Assert.Single(expected.CommitRecords);
        var actualRecord = Assert.Single(actual.CommitRecords);
        Assert.Equal(expectedRecord.Key, actualRecord.Key);
        Assert.Equal(expectedRecord.Value.Timestamp, actualRecord.Value.Timestamp);
        Assert.Equal(expectedRecord.Value.WriteParticipants, actualRecord.Value.WriteParticipants);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSerializer();
        services.Configure<ClusterOptions>(options => options.ServiceId = "service");
        services.Configure<DynamoDBTransactionalStorageOptions>(
            ProviderName,
            options =>
            {
                var configured = CreateOptions();
                options.Service = configured.Service;
                options.AccessKey = configured.AccessKey;
                options.SecretKey = configured.SecretKey;
                options.TableName = configured.TableName;
                options.CreateIfNotExists = configured.CreateIfNotExists;
                options.UpdateIfExists = configured.UpdateIfExists;
                options.GrainStorageSerializer = configured.GrainStorageSerializer;
            });
        return services.BuildServiceProvider();
    }

    private sealed class RecordingFaultInjector(List<string> events)
        : ITransactionFaultInjector, ITransactionScopedFaultInjector
    {
        public List<IReadOnlyList<Guid>> BeforeTransactionIds { get; } = [];

        public List<IReadOnlyList<Guid>> AfterTransactionIds { get; } = [];

        public void BeforeStore() => throw new InvalidOperationException(
            "The transaction-scoped injector path was expected.");

        public void AfterStore() => throw new InvalidOperationException(
            "The transaction-scoped injector path was expected.");

        public void Arm(
            Guid transactionId,
            FaultInjectionType injectionType,
            bool requireTransactionMatch) =>
            throw new NotSupportedException();

        public void BeforeStore(System.Collections.Immutable.ImmutableArray<Guid> transactionIds)
        {
            BeforeTransactionIds.Add(transactionIds);
            events.Add("before");
        }

        public void AfterStore(System.Collections.Immutable.ImmutableArray<Guid> transactionIds)
        {
            AfterTransactionIds.Add(transactionIds);
            events.Add("after");
        }
    }

    private sealed class RecordingAmazonDynamoDBClient(List<string> events)
        : AmazonDynamoDBClient(
            new BasicAWSCredentials("dummy", "dummy"),
            new AmazonDynamoDBConfig
            {
                ServiceURL = "http://127.0.0.1:65535",
            })
    {
        public List<GetItemRequest> GetItemCalls { get; } = [];

        public List<QueryRequest> QueryCalls { get; } = [];

        public List<TransactWriteItemsRequest> TransactWriteItemsCalls { get; } = [];

        public override Task<GetItemResponse> GetItemAsync(
            GetItemRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetItemCalls.Add(request);
            return Task.FromResult(new GetItemResponse());
        }

        public override Task<QueryResponse> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls.Add(request);
            return Task.FromResult(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = [],
            });
        }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
            TransactWriteItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactWriteItemsCalls.Add(request);
            events.Add("storage");
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }

    private sealed class RecordingSiloLifecycle : ISiloLifecycle
    {
        private ILifecycleObserver? _observer;

        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public int? Stage { get; private set; }

        public IDisposable Subscribe(
            string observerName,
            int stage,
            ILifecycleObserver observer)
        {
            Assert.Null(_observer);
            Assert.Contains(nameof(DynamoDBTransactionalStateStorageFactory), observerName);
            Stage = stage;
            _observer = observer;
            return NoopDisposable.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            Assert.IsAssignableFrom<ILifecycleObserver>(_observer).OnStart(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class TestState
    {
        public string Value { get; set; } = string.Empty;
    }
}
