using System.Reflection;
using System.Runtime.ExceptionServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Transactions.DynamoDB;
using Xunit;

namespace Orleans.Transactions.DynamoDB.Tests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Transactions")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
[TestCategory("Transactions")]
[TestCategory("DynamoDBStorage")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("xUnit", "xUnit1051", Justification = "These tests intentionally pass explicit CancellationToken instances to verify propagation, cancellation, and identity semantics.")]
public sealed class DynamoDBStorageUnitTests
{
    private const string TableName = "unit-test-table";
    private static readonly List<AttributeDefinition> Attributes =
    [
        new("pk", ScalarAttributeType.S),
        new("gsi-pk", ScalarAttributeType.S)
    ];

    [Fact]
    public async Task UpdateTableAsync_UpdateIfExistsFalse_ReturnsWithoutClientCall()
    {
        var (storage, client) = CreateStorage(updateIfExists: false);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes);

        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }
    [Fact]
    public async Task UpdateTableAsync_UnsupportedTableStatus_ThrowsBeforeClientCall()
    {
        var (storage, client) = CreateStorage();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeUpdateTableAsync(storage, Table(TableStatus.DELETING), Attributes));

        Assert.Equal($"Table {TableName} has a status of {TableStatus.DELETING} and can't be updated automatically.", exception.Message);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_CreatingStatus_WaitsBeforeEvaluatingChanges()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE))));
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.CREATING), Attributes, cancellationToken: source.Token);

        AssertCallOrder(client, "DescribeTable", "DescribeTimeToLive");
        Assert.All(client.Calls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_UpdatingStatus_WaitsBeforeEvaluatingChanges()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE))));
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.UPDATING), Attributes, cancellationToken: source.Token);

        AssertCallOrder(client, "DescribeTable", "DescribeTimeToLive");
        Assert.All(client.Calls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_ActiveStatus_SkipsInitialTableWait()
    {
        var (storage, client) = CreateStorage();
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes);

        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_RequestedReadCapacityDiffers_SubmitsProvisionedRequestAndWaits()
    {
        var (storage, client) = CreateStorage(readCapacityUnits: 17, writeCapacityUnits: 23);
        using var source = new CancellationTokenSource();
        var existingIndexes = new List<GlobalSecondaryIndexDescription>
        {
            IndexDescription("first", IndexStatus.ACTIVE),
            IndexDescription("second", IndexStatus.ACTIVE)
        };
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromResult(new UpdateTableResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, 17, 23, existingIndexes))));
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(
            storage,
            Table(TableStatus.ACTIVE, read: 10, write: 23, indexes: existingIndexes),
            Attributes,
            cancellationToken: source.Token);

        AssertCallOrder(client, "UpdateTable", "DescribeTable", "DescribeTimeToLive");
        var request = Assert.Single(client.UpdateTableCalls).Request;
        Assert.Equal(TableName, request.TableName);
        Assert.Equal(BillingMode.PROVISIONED, request.BillingMode);
        Assert.Equal(17, request.ProvisionedThroughput.ReadCapacityUnits);
        Assert.Equal(23, request.ProvisionedThroughput.WriteCapacityUnits);
        Assert.Equal(["first", "second"], request.GlobalSecondaryIndexUpdates.Select(update => update.Update.IndexName));
        Assert.All(request.GlobalSecondaryIndexUpdates, update => Assert.Same(request.ProvisionedThroughput, update.Update.ProvisionedThroughput));
        Assert.All(client.Calls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_RequestedWriteCapacityDiffers_SubmitsProvisionedRequestAndWaits()
    {
        var (storage, client) = CreateStorage(readCapacityUnits: 10, writeCapacityUnits: 19);
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromResult(new UpdateTableResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, 10, 19))));
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE, read: 10, write: 5), Attributes);

        AssertCallOrder(client, "UpdateTable", "DescribeTable", "DescribeTimeToLive");
        var request = Assert.Single(client.UpdateTableCalls).Request;
        Assert.Equal(BillingMode.PROVISIONED, request.BillingMode);
        Assert.Equal(10, request.ProvisionedThroughput.ReadCapacityUnits);
        Assert.Equal(19, request.ProvisionedThroughput.WriteCapacityUnits);
        Assert.Null(request.GlobalSecondaryIndexUpdates);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_SwitchingProvisionedTableToOnDemand_SubmitsPayPerRequestShape()
    {
        var (storage, client) = CreateStorage(useProvisionedThroughput: false);
        using var source = new CancellationTokenSource();
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromResult(new UpdateTableResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, read: 0, write: 0))));
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE, read: 10, write: 5), Attributes, cancellationToken: source.Token);

        AssertCallOrder(client, "UpdateTable", "DescribeTable", "DescribeTimeToLive");
        var call = Assert.Single(client.UpdateTableCalls);
        Assert.Equal(BillingMode.PAY_PER_REQUEST, call.Request.BillingMode);
        Assert.Null(call.Request.ProvisionedThroughput);
        Assert.Null(call.Request.GlobalSecondaryIndexUpdates);
        Assert.Equal(source.Token, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_UnchangedCapacity_SkipsCapacityUpdateAndWait()
    {
        var (storage, client) = CreateStorage(readCapacityUnits: 10, writeCapacityUnits: 5);
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE, read: 10, write: 5), Attributes);

        Assert.Empty(client.UpdateTableCalls);
        Assert.Empty(client.DescribeTableCalls);
        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_NullExistingIndexesAndNullRequestedIndexes_PerformsNoIndexWork()
    {
        var (storage, client) = CreateStorage();
        EnqueueDisabledTtl(client);

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE, indexes: null), Attributes, secondaryIndexes: null);

        Assert.Empty(client.UpdateTableCalls);
        Assert.Empty(client.DescribeTableCalls);
        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_ExistingIndexesInCreatingUpdatingAndActiveStates_WaitsOnlyForTransientIndexes()
    {
        var (storage, client) = CreateStorage();
        var initialIndexes = new List<GlobalSecondaryIndexDescription>
        {
            IndexDescription("creating", IndexStatus.CREATING),
            IndexDescription("updating", IndexStatus.UPDATING),
            IndexDescription("active", IndexStatus.ACTIVE)
        };
        EnqueueDisabledTtl(client);
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(
            TableStatus.ACTIVE,
            indexes:
            [
                IndexDescription("creating", IndexStatus.ACTIVE),
                IndexDescription("updating", IndexStatus.UPDATING),
                IndexDescription("active", IndexStatus.ACTIVE)
            ]))));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(
            TableStatus.ACTIVE,
            indexes:
            [
                IndexDescription("creating", IndexStatus.ACTIVE),
                IndexDescription("updating", IndexStatus.ACTIVE),
                IndexDescription("active", IndexStatus.ACTIVE)
            ]))));

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE, indexes: initialIndexes), Attributes);

        AssertCallOrder(client, "DescribeTimeToLive", "DescribeTable", "DescribeTable");
        Assert.Equal(2, client.DescribeTableCalls.Count);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_MissingRequestedIndexes_CreatesOnlyMissingIndexesInOrder()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        var existing = RequestedIndex("existing");
        var missingOne = RequestedIndex("missing-one");
        var missingTwo = RequestedIndex("missing-two");
        EnqueueDisabledTtl(client);
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromResult(new UpdateTableResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, indexes:
        [
            IndexDescription("existing", IndexStatus.ACTIVE),
            IndexDescription("missing-one", IndexStatus.ACTIVE)
        ]))));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, indexes:
        [
            IndexDescription("existing", IndexStatus.ACTIVE),
            IndexDescription("missing-one", IndexStatus.ACTIVE)
        ]))));
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromResult(new UpdateTableResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, indexes:
        [
            IndexDescription("existing", IndexStatus.ACTIVE),
            IndexDescription("missing-one", IndexStatus.ACTIVE),
            IndexDescription("missing-two", IndexStatus.ACTIVE)
        ]))));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, indexes:
        [
            IndexDescription("existing", IndexStatus.ACTIVE),
            IndexDescription("missing-one", IndexStatus.ACTIVE),
            IndexDescription("missing-two", IndexStatus.ACTIVE)
        ]))));

        await InvokeUpdateTableAsync(
            storage,
            Table(TableStatus.ACTIVE, indexes: [IndexDescription("existing", IndexStatus.ACTIVE)]),
            Attributes,
            [existing, missingOne, missingTwo],
            cancellationToken: source.Token);

        AssertCallOrder(
            client,
            "DescribeTimeToLive",
            "UpdateTable",
            "DescribeTable",
            "DescribeTable",
            "UpdateTable",
            "DescribeTable",
            "DescribeTable");
        Assert.Equal(["missing-one", "missing-two"], client.UpdateTableCalls.Select(call => Assert.Single(call.Request.GlobalSecondaryIndexUpdates).Create.IndexName));
        Assert.Same(missingOne.Projection, client.UpdateTableCalls[0].Request.GlobalSecondaryIndexUpdates[0].Create.Projection);
        Assert.Same(missingOne.KeySchema, client.UpdateTableCalls[0].Request.GlobalSecondaryIndexUpdates[0].Create.KeySchema);
        Assert.Same(missingTwo.Projection, client.UpdateTableCalls[1].Request.GlobalSecondaryIndexUpdates[0].Create.Projection);
        Assert.Same(missingTwo.KeySchema, client.UpdateTableCalls[1].Request.GlobalSecondaryIndexUpdates[0].Create.KeySchema);
        Assert.All(client.Calls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateTableAsync_NullOrEmptyRequestedIndexes_CreatesNoIndexes(bool useNull)
    {
        var (storage, client) = CreateStorage();
        EnqueueDisabledTtl(client);
        var requested = useNull ? null : new List<GlobalSecondaryIndex>();

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, requested);

        Assert.Empty(client.UpdateTableCalls);
        Assert.Empty(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_TtlNonDisabledOnWrongAttribute_DoesNotUpdateTtl()
    {
        var (storage, client) = CreateStorage();
        EnqueueTtl(client, TimeToLiveStatus.ENABLED, "old-ttl");

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, ttlAttributeName: "new-ttl");

        Assert.Empty(client.UpdateTimeToLiveCalls);
        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_EmptyTtlDescription_DoesNotUpdateTtl()
    {
        var (storage, client) = CreateStorage();
        client.DescribeTimeToLiveScripts.Enqueue((_, _) => Task.FromResult(new DescribeTimeToLiveResponse
        {
            TimeToLiveDescription = new TimeToLiveDescription()
        }));

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, ttlAttributeName: "ttl");

        Assert.Empty(client.UpdateTimeToLiveCalls);
        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_TtlAlreadyEnabledOnRequestedAttribute_DoesNotUpdateTtl()
    {
        var (storage, client) = CreateStorage();
        EnqueueTtl(client, TimeToLiveStatus.ENABLED, "ttl");

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, ttlAttributeName: "ttl");

        Assert.Empty(client.UpdateTimeToLiveCalls);
        AssertCallOrder(client, "DescribeTimeToLive");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_DisabledTtl_EnablesRequestedAttributeAndWaits()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        EnqueueTtl(client, TimeToLiveStatus.DISABLED, null);
        client.UpdateTimeToLiveScripts.Enqueue((_, _) => Task.FromResult(new UpdateTimeToLiveResponse()));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE))));

        await InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, ttlAttributeName: "expires-at", cancellationToken: source.Token);

        AssertCallOrder(client, "DescribeTimeToLive", "UpdateTimeToLive", "DescribeTable");
        var call = Assert.Single(client.UpdateTimeToLiveCalls);
        Assert.Equal(TableName, call.Request.TableName);
        Assert.Equal("expires-at", call.Request.TimeToLiveSpecification.AttributeName);
        Assert.True(call.Request.TimeToLiveSpecification.Enabled);
        Assert.All(client.Calls, recorded => Assert.Equal(source.Token, recorded.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_TtlAmazonServiceFailure_IsSwallowed()
    {
        var (storage, client) = CreateStorage();
        var exception = new AmazonDynamoDBException("ttl service failure");
        EnqueueTtl(client, TimeToLiveStatus.DISABLED, null);
        client.UpdateTimeToLiveScripts.Enqueue((_, _) => Task.FromException<UpdateTimeToLiveResponse>(exception));
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE, indexes:
        [
            IndexDescription("building", IndexStatus.ACTIVE)
        ]))));

        await InvokeUpdateTableAsync(
            storage,
            Table(TableStatus.ACTIVE, indexes: [IndexDescription("building", IndexStatus.CREATING)]),
            Attributes,
            ttlAttributeName: "ttl");

        AssertCallOrder(client, "DescribeTimeToLive", "UpdateTimeToLive", "DescribeTable");
        Assert.Single(client.UpdateTimeToLiveCalls);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_CanceledClientTask_PropagatesCancellationAndToken()
    {
        var (storage, client) = CreateStorage(readCapacityUnits: 20);
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.UpdateTableScripts.Enqueue((_, token) => Task.FromCanceled<UpdateTableResponse>(token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, cancellationToken: source.Token));

        Assert.Equal(source.Token, exception.CancellationToken);
        var call = Assert.Single(client.UpdateTableCalls);
        Assert.Equal(source.Token, call.CancellationToken);
        AssertCallOrder(client, "UpdateTable");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpdateTableAsync_AmazonServiceFailure_RethrowsSameException()
    {
        var (storage, client) = CreateStorage(readCapacityUnits: 20);
        using var source = new CancellationTokenSource();
        var expected = new AmazonDynamoDBException("update service failure");
        client.UpdateTableScripts.Enqueue((_, _) => Task.FromException<UpdateTableResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => InvokeUpdateTableAsync(storage, Table(TableStatus.ACTIVE), Attributes, cancellationToken: source.Token));

        Assert.Same(expected, actual);
        AssertCallOrder(client, "UpdateTable");
        Assert.Equal(source.Token, Assert.Single(client.UpdateTableCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task InitializeTable_ExistingTable_RoutesToUpdateAndPropagatesToken()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE))));
        EnqueueDisabledTtl(client);

        await storage.InitializeTable(
            TableName,
            [new KeySchemaElement("pk", KeyType.HASH)],
            Attributes,
            cancellationToken: source.Token);

        AssertCallOrder(client, "DescribeTable", "DescribeTimeToLive");
        Assert.Empty(client.UpdateTableCalls);
        Assert.All(client.Calls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_MissingIndexAndNullDesiredStatus_ReturnsWithoutDelay()
    {
        var (storage, client) = CreateStorage();
        var description = Table(TableStatus.ACTIVE);
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(description)));

        var result = await InvokeTableIndexWaitOnStatusAsync(storage, "missing", IndexStatus.CREATING, desiredStatus: null);

        Assert.Same(description, result);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_IndexOutsideWhileStatusAndNullDesiredStatus_ReturnsWithoutDelay()
    {
        var (storage, client) = CreateStorage();
        var description = Table(TableStatus.ACTIVE, indexes: [IndexDescription("index", IndexStatus.DELETING)]);
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(description)));

        var result = await InvokeTableIndexWaitOnStatusAsync(storage, "index", IndexStatus.CREATING, desiredStatus: null);

        Assert.Same(description, result);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_IndexInitiallyInWhileStatus_DescribesUntilTransition()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(
            TableStatus.ACTIVE,
            indexes: [IndexDescription("index", IndexStatus.CREATING)]))));
        var final = Table(TableStatus.ACTIVE, indexes: [IndexDescription("index", IndexStatus.ACTIVE)]);
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(final)));

        var result = await InvokeTableIndexWaitOnStatusAsync(
            storage,
            "index",
            IndexStatus.CREATING,
            IndexStatus.ACTIVE,
            cancellationToken: source.Token);

        Assert.Same(final, result);
        Assert.Equal(2, client.DescribeTableCalls.Count);
        Assert.All(client.DescribeTableCalls, call => Assert.Equal(source.Token, call.CancellationToken));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_IndexAlreadyAtDesiredStatus_Returns()
    {
        var (storage, client) = CreateStorage();
        var description = Table(TableStatus.ACTIVE, indexes: [IndexDescription("index", IndexStatus.ACTIVE)]);
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(description)));

        var result = await InvokeTableIndexWaitOnStatusAsync(storage, "index", IndexStatus.CREATING, IndexStatus.ACTIVE);

        Assert.Same(description, result);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_MissingIndexWithDesiredStatus_Throws()
    {
        var (storage, client) = CreateStorage();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(TableStatus.ACTIVE))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeTableIndexWaitOnStatusAsync(storage, "missing", IndexStatus.CREATING, IndexStatus.ACTIVE));

        Assert.Equal($"Index missing in table {TableName} has failed to reach the desired status of {IndexStatus.ACTIVE}", exception.Message);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_WrongFinalStatus_Throws()
    {
        var (storage, client) = CreateStorage();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(
            TableStatus.ACTIVE,
            indexes: [IndexDescription("index", IndexStatus.DELETING)]))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeTableIndexWaitOnStatusAsync(storage, "index", IndexStatus.CREATING, IndexStatus.ACTIVE));

        Assert.Equal($"Index index in table {TableName} has failed to reach the desired status of {IndexStatus.ACTIVE}", exception.Message);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_DescribeServiceFailure_RethrowsSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("describe failed");
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromException<DescribeTableResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => InvokeTableIndexWaitOnStatusAsync(storage, "index", IndexStatus.CREATING, IndexStatus.ACTIVE));

        Assert.Same(expected, actual);
        Assert.Single(client.DescribeTableCalls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_DescribeCancellation_PropagatesAndPreservesToken()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.DescribeTableScripts.Enqueue((_, token) => Task.FromCanceled<DescribeTableResponse>(token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeTableIndexWaitOnStatusAsync(
                storage,
                "index",
                IndexStatus.CREATING,
                IndexStatus.ACTIVE,
                cancellationToken: source.Token));

        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(source.Token, Assert.Single(client.DescribeTableCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task TableIndexWaitOnStatusAsync_CancellationDuringDelay_Propagates()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.DescribeTableScripts.Enqueue((_, _) => Task.FromResult(Describe(Table(
            TableStatus.ACTIVE,
            indexes: [IndexDescription("index", IndexStatus.CREATING)]))));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeTableIndexWaitOnStatusAsync(
                storage,
                "index",
                IndexStatus.CREATING,
                IndexStatus.ACTIVE,
                cancellationToken: source.Token));

        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(source.Token, Assert.Single(client.DescribeTableCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void PutEntriesAsync_NullEntries_ThrowsArgumentNullException()
    {
        var (storage, client) = CreateStorage();

        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = storage.PutEntriesAsync(TableName, null!);
        });

        Assert.Equal("toCreate", exception.ParamName);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task PutEntriesAsync_EmptyEntries_CompletesWithoutClientCall()
    {
        var (storage, client) = CreateStorage();

        await storage.PutEntriesAsync(TableName, []);

        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task PutEntriesAsync_NonEmptyEntries_SubmitsOrderedPutRequestsForExactTable()
    {
        var (storage, client) = CreateStorage();
        var first = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue("one") };
        var second = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue("two") };
        client.BatchWriteItemScripts.Enqueue((_, _) => Task.FromResult(new BatchWriteItemResponse()));

        await storage.PutEntriesAsync(TableName, [first, second]);

        AssertCallOrder(client, "BatchWriteItem");
        var call = Assert.Single(client.BatchWriteItemCalls);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        var tableEntry = Assert.Single(call.Request.RequestItems);
        Assert.Equal(TableName, tableEntry.Key);
        Assert.Equal(2, tableEntry.Value.Count);
        Assert.Same(first, tableEntry.Value[0].PutRequest.Item);
        Assert.Same(second, tableEntry.Value[1].PutRequest.Item);
        Assert.All(tableEntry.Value, item => Assert.Null(item.DeleteRequest));
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void PutEntriesAsync_SynchronousClientFailure_ThrowsSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("synchronous batch failure");
        client.BatchWriteItemScripts.Enqueue((_, _) => throw expected);

        var actual = Assert.Throws<AmazonDynamoDBException>(() =>
        {
            _ = storage.PutEntriesAsync(TableName, [new Dictionary<string, AttributeValue>()]);
        });

        Assert.Same(expected, actual);
        AssertCallOrder(client, "BatchWriteItem");
        Assert.Equal(CancellationToken.None, Assert.Single(client.BatchWriteItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task PutEntriesAsync_FaultedClientTask_PropagatesSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("asynchronous batch failure");
        client.BatchWriteItemScripts.Enqueue((_, _) => Task.FromException<BatchWriteItemResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => storage.PutEntriesAsync(TableName, [new Dictionary<string, AttributeValue>()]));

        Assert.Same(expected, actual);
        AssertCallOrder(client, "BatchWriteItem");
        Assert.Equal(CancellationToken.None, Assert.Single(client.BatchWriteItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task PutEntriesAsync_CanceledClientTask_PropagatesCancellation()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.BatchWriteItemScripts.Enqueue((_, _) => Task.FromCanceled<BatchWriteItemResponse>(source.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.PutEntriesAsync(TableName, [new Dictionary<string, AttributeValue>()]));

        Assert.Equal(source.Token, exception.CancellationToken);
        AssertCallOrder(client, "BatchWriteItem");
        Assert.Equal(CancellationToken.None, Assert.Single(client.BatchWriteItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_AllComponentInputsNull_SubmitsEmptyTransaction()
    {
        var (storage, client) = CreateStorage();
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync();

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.NotNull(call.Request.TransactItems);
        Assert.Empty(call.Request.TransactItems);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        AssertCallOrder(client, "TransactWriteItems");
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_AllComponentInputsEmpty_SubmitsEmptyTransaction()
    {
        var (storage, client) = CreateStorage();
        Put[] puts = [];
        Update[] updates = [];
        Delete[] deletes = [];
        ConditionCheck[] checks = [];
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync(puts, updates, deletes, checks);

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.Empty(call.Request.TransactItems);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_MixedComponents_GroupsItemsAndPreservesGroupOrder()
    {
        var (storage, client) = CreateStorage();
        var putOne = new Put { TableName = "put-one" };
        var putTwo = new Put { TableName = "put-two" };
        var update = new Update { TableName = "update" };
        var deleteOne = new Delete { TableName = "delete-one" };
        var deleteTwo = new Delete { TableName = "delete-two" };
        var check = new ConditionCheck { TableName = "check" };
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync([putOne, putTwo], [update], [deleteOne, deleteTwo], [check]);

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.Equal(6, call.Request.TransactItems.Count);
        Assert.Same(putOne, call.Request.TransactItems[0].Put);
        Assert.Same(putTwo, call.Request.TransactItems[1].Put);
        Assert.Same(update, call.Request.TransactItems[2].Update);
        Assert.Same(deleteOne, call.Request.TransactItems[3].Delete);
        Assert.Same(deleteTwo, call.Request.TransactItems[4].Delete);
        Assert.Same(check, call.Request.TransactItems[5].ConditionCheck);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void WriteTxAsync_DeferredEnumerationFailure_ThrowsSynchronouslyWithoutClientCall()
    {
        var (storage, client) = CreateStorage();
        var expected = new InvalidOperationException("deferred enumeration failed");

        var actual = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = storage.WriteTxAsync(ThrowOnEnumeration<Put>(expected));
        });

        Assert.Same(expected, actual);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void WriteTxAsync_ComponentOverloadSynchronousClientFailure_ThrowsSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("synchronous transaction failure");
        client.TransactWriteItemsScripts.Enqueue((_, _) => throw expected);

        var actual = Assert.Throws<AmazonDynamoDBException>(() =>
        {
            _ = storage.WriteTxAsync(puts: [new Put()]);
        });

        Assert.Same(expected, actual);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ComponentOverloadFaultedClientTask_PropagatesSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("asynchronous transaction failure");
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromException<TransactWriteItemsResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => storage.WriteTxAsync(puts: [new Put()]));

        Assert.Same(expected, actual);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ComponentOverloadCanceledClientTask_PropagatesCancellation()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromCanceled<TransactWriteItemsResponse>(source.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.WriteTxAsync(puts: [new Put()]));

        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ListOverload_ForwardsSuppliedListByIdentity()
    {
        var (storage, client) = CreateStorage();
        var first = new TransactWriteItem { Put = new Put { TableName = "first" } };
        var second = new TransactWriteItem { Delete = new Delete { TableName = "second" } };
        var items = new List<TransactWriteItem> { first, second };
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync(items);

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.Same(items, call.Request.TransactItems);
        Assert.Same(first, call.Request.TransactItems[0]);
        Assert.Same(second, call.Request.TransactItems[1]);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ListOverload_EmptyList_SubmitsEmptyListByIdentity()
    {
        var (storage, client) = CreateStorage();
        var items = new List<TransactWriteItem>();
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync(items);

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.Same(items, call.Request.TransactItems);
        Assert.Empty(call.Request.TransactItems);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ListOverload_NullListAtRuntime_SubmitsNullTransactItems()
    {
        var (storage, client) = CreateStorage();
        List<TransactWriteItem> items = null!;
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromResult(new TransactWriteItemsResponse()));

        await storage.WriteTxAsync(items);

        var call = Assert.Single(client.TransactWriteItemsCalls);
        Assert.Null(call.Request.TransactItems);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void WriteTxAsync_ListOverloadSynchronousClientFailure_ThrowsSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("synchronous list transaction failure");
        client.TransactWriteItemsScripts.Enqueue((_, _) => throw expected);

        var actual = Assert.Throws<AmazonDynamoDBException>(() =>
        {
            _ = storage.WriteTxAsync(new List<TransactWriteItem>());
        });

        Assert.Same(expected, actual);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ListOverloadFaultedClientTask_PropagatesSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("asynchronous list transaction failure");
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromException<TransactWriteItemsResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => storage.WriteTxAsync(new List<TransactWriteItem>()));

        Assert.Same(expected, actual);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task WriteTxAsync_ListOverloadCanceledClientTask_PropagatesCancellation()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.TransactWriteItemsScripts.Enqueue((_, _) => Task.FromCanceled<TransactWriteItemsResponse>(source.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.WriteTxAsync(new List<TransactWriteItem>()));

        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(CancellationToken.None, Assert.Single(client.TransactWriteItemsCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_OneField_ProducesSetExpressionWithoutTrailingComma()
    {
        var (storage, client) = CreateStorage();
        var value = new AttributeValue("value");

        var (expression, values) = storage.ConvertUpdate(new Dictionary<string, AttributeValue> { ["field"] = value });

        Assert.Equal("SET field = :field", expression);
        var entry = Assert.Single(values);
        Assert.Equal(":field", entry.Key);
        Assert.Same(value, entry.Value);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_MultipleFields_PreservesDictionaryOrder()
    {
        var (storage, client) = CreateStorage();
        var first = new AttributeValue("one");
        var second = new AttributeValue("two");
        var fields = new Dictionary<string, AttributeValue>
        {
            ["first"] = first,
            ["second"] = second
        };

        var (expression, values) = storage.ConvertUpdate(fields);

        Assert.Equal("SET first = :first, second = :second", expression);
        Assert.Equal([":first", ":second"], values.Keys);
        Assert.Same(first, values[":first"]);
        Assert.Same(second, values[":second"]);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void ConvertUpdate_NullEmptyOrWhitespaceExtraExpression_UsesNoExtraBranch(string? extraExpression)
    {
        var (storage, client) = CreateStorage();
        var field = new AttributeValue("value");
        var ignored = new AttributeValue("ignored");

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = field },
            extraExpression: extraExpression!,
            extraExpressionValues: new Dictionary<string, AttributeValue> { [":ignored"] = ignored });

        Assert.Equal("SET field = :field", expression);
        Assert.Single(values);
        Assert.Same(field, values[":field"]);
        Assert.DoesNotContain(":ignored", values);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_NonblankExtraExpression_AppendsSetAssignment()
    {
        var (storage, client) = CreateStorage();

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            extraExpression: "other = field");

        Assert.Equal("SET field = :field, other = field", expression);
        Assert.Single(values);
        Assert.Contains(":field", values);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConvertUpdate_NullOrEmptyExtraValues_AddsNoExtraValues(bool useNull)
    {
        var (storage, client) = CreateStorage();
        var extraValues = useNull ? null : new Dictionary<string, AttributeValue>();

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            extraExpression: "other = field",
            extraExpressionValues: extraValues);

        Assert.Equal("SET field = :field, other = field", expression);
        Assert.Equal([":field"], values.Keys);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_PopulatedExtraValues_AddsEveryEntry()
    {
        var (storage, client) = CreateStorage();
        var increment = new AttributeValue { N = "3" };
        var replacement = new AttributeValue("replacement");

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            extraExpression: "counter = counter + :increment, other = :replacement",
            extraExpressionValues: new Dictionary<string, AttributeValue>
            {
                [":increment"] = increment,
                [":replacement"] = replacement
            });

        Assert.Equal("SET field = :field, counter = counter + :increment, other = :replacement", expression);
        Assert.Equal([":field", ":increment", ":replacement"], values.Keys);
        Assert.Same(increment, values[":increment"]);
        Assert.Same(replacement, values[":replacement"]);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConvertUpdate_NullOrEmptyConditionValues_AddsNoConditionValues(bool useNull)
    {
        var (storage, client) = CreateStorage();
        var conditionValues = useNull ? null : new Dictionary<string, AttributeValue>();

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            conditionValues);

        Assert.Equal("SET field = :field", expression);
        Assert.Equal([":field"], values.Keys);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_PopulatedConditionValues_AddsEveryEntry()
    {
        var (storage, client) = CreateStorage();
        var expectedVersion = new AttributeValue { N = "7" };
        var expectedOwner = new AttributeValue("owner");

        var (expression, values) = storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            new Dictionary<string, AttributeValue>
            {
                [":version"] = expectedVersion,
                [":owner"] = expectedOwner
            });

        Assert.Equal("SET field = :field", expression);
        Assert.Equal([":field", ":version", ":owner"], values.Keys);
        Assert.Same(expectedVersion, values[":version"]);
        Assert.Same(expectedOwner, values[":owner"]);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_DuplicateGeneratedAndExtraValueKey_ThrowsArgumentException()
    {
        var (storage, client) = CreateStorage();

        var exception = Assert.Throws<ArgumentException>(() => storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            extraExpression: "other = :field",
            extraExpressionValues: new Dictionary<string, AttributeValue> { [":field"] = new("duplicate") }));

        Assert.Null(exception.ParamName);
        Assert.Contains(":field", exception.Message);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_DuplicateGeneratedAndConditionValueKey_ThrowsArgumentException()
    {
        var (storage, client) = CreateStorage();

        var exception = Assert.Throws<ArgumentException>(() => storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            new Dictionary<string, AttributeValue> { [":field"] = new("duplicate") }));

        Assert.Null(exception.ParamName);
        Assert.Contains(":field", exception.Message);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_DuplicateExtraAndConditionValueKey_ThrowsArgumentException()
    {
        var (storage, client) = CreateStorage();

        var exception = Assert.Throws<ArgumentException>(() => storage.ConvertUpdate(
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            new Dictionary<string, AttributeValue> { [":shared"] = new("condition") },
            "counter = :shared",
            new Dictionary<string, AttributeValue> { [":shared"] = new("extra") }));

        Assert.Null(exception.ParamName);
        Assert.Contains(":shared", exception.Message);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public void ConvertUpdate_EmptyFieldsWithoutExtraExpression_ReturnsCurrentSECharacterization()
    {
        var (storage, client) = CreateStorage();

        var (expression, values) = storage.ConvertUpdate([]);

        Assert.Equal("SE", expression);
        Assert.Empty(values);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_BlankCondition_SubmitsUnconditionalUpdateShape()
    {
        var (storage, client) = CreateStorage();
        var keys = new Dictionary<string, AttributeValue> { ["pk"] = new("one") };
        var field = new AttributeValue("value");
        var increment = new AttributeValue { N = "2" };
        var fields = new Dictionary<string, AttributeValue> { ["field"] = field };
        client.UpdateItemScripts.Enqueue((_, _) => Task.FromResult(new UpdateItemResponse
        {
            Attributes = new Dictionary<string, AttributeValue>()
        }));

        await storage.UpsertEntryAsync(
            TableName,
            keys,
            fields,
            conditionExpression: " \t ",
            extraExpression: "counter = counter + :increment",
            extraExpressionValues: new Dictionary<string, AttributeValue> { [":increment"] = increment });

        AssertCallOrder(client, "UpdateItem");
        var call = Assert.Single(client.UpdateItemCalls);
        Assert.Equal(TableName, call.Request.TableName);
        Assert.Same(keys, call.Request.Key);
        Assert.Equal(ReturnValue.UPDATED_NEW, call.Request.ReturnValues);
        Assert.Equal("SET field = :field, counter = counter + :increment", call.Request.UpdateExpression);
        Assert.Null(call.Request.ConditionExpression);
        Assert.Equal([":field", ":increment"], call.Request.ExpressionAttributeValues.Keys);
        Assert.Same(field, call.Request.ExpressionAttributeValues[":field"]);
        Assert.Same(increment, call.Request.ExpressionAttributeValues[":increment"]);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_NonblankCondition_SubmitsConditionalUpdateShape()
    {
        var (storage, client) = CreateStorage();
        var keys = new Dictionary<string, AttributeValue> { ["pk"] = new("one") };
        var field = new AttributeValue("value");
        var expectedVersion = new AttributeValue { N = "11" };
        var fields = new Dictionary<string, AttributeValue> { ["field"] = field };
        client.UpdateItemScripts.Enqueue((_, _) => Task.FromResult(new UpdateItemResponse
        {
            Attributes = new Dictionary<string, AttributeValue>()
        }));

        await storage.UpsertEntryAsync(
            TableName,
            keys,
            fields,
            "version = :expectedVersion",
            new Dictionary<string, AttributeValue> { [":expectedVersion"] = expectedVersion });

        var call = Assert.Single(client.UpdateItemCalls);
        Assert.Equal("version = :expectedVersion", call.Request.ConditionExpression);
        Assert.Equal("SET field = :field", call.Request.UpdateExpression);
        Assert.Equal([":field", ":expectedVersion"], call.Request.ExpressionAttributeValues.Keys);
        Assert.Same(field, call.Request.ExpressionAttributeValues[":field"]);
        Assert.Same(expectedVersion, call.Request.ExpressionAttributeValues[":expectedVersion"]);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_ResponseAttributes_ReplacesExistingKeysAndAddsNewKeys()
    {
        var (storage, client) = CreateStorage();
        var original = new AttributeValue("original");
        var untouched = new AttributeValue("untouched");
        var replacement = new AttributeValue("replacement");
        var added = new AttributeValue("added");
        var fields = new Dictionary<string, AttributeValue>
        {
            ["replace"] = original,
            ["untouched"] = untouched
        };
        client.UpdateItemScripts.Enqueue((_, _) => Task.FromResult(new UpdateItemResponse
        {
            Attributes = new Dictionary<string, AttributeValue>
            {
                ["replace"] = replacement,
                ["added"] = added
            }
        }));

        await storage.UpsertEntryAsync(
            TableName,
            new Dictionary<string, AttributeValue> { ["pk"] = new("one") },
            fields);

        Assert.Equal(3, fields.Count);
        Assert.Same(replacement, fields["replace"]);
        Assert.Same(untouched, fields["untouched"]);
        Assert.Same(added, fields["added"]);
        Assert.Equal(CancellationToken.None, Assert.Single(client.UpdateItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_ConversionFailure_ThrowsBeforeClientCall()
    {
        var (storage, client) = CreateStorage();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => storage.UpsertEntryAsync(
            TableName,
            new Dictionary<string, AttributeValue> { ["pk"] = new("one") },
            new Dictionary<string, AttributeValue> { ["field"] = new("value") },
            conditionValues: new Dictionary<string, AttributeValue> { [":field"] = new("duplicate") }));

        Assert.Null(exception.ParamName);
        Assert.Contains(":field", exception.Message);
        Assert.Empty(client.Calls);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_AmazonServiceFailure_RethrowsSameException()
    {
        var (storage, client) = CreateStorage();
        var expected = new AmazonDynamoDBException("update item failed");
        client.UpdateItemScripts.Enqueue((_, _) => Task.FromException<UpdateItemResponse>(expected));

        var actual = await Assert.ThrowsAsync<AmazonDynamoDBException>(() => storage.UpsertEntryAsync(
            TableName,
            new Dictionary<string, AttributeValue> { ["pk"] = new("one") },
            new Dictionary<string, AttributeValue> { ["field"] = new("value") }));

        Assert.Same(expected, actual);
        AssertCallOrder(client, "UpdateItem");
        Assert.Equal(CancellationToken.None, Assert.Single(client.UpdateItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    [Fact]
    public async Task UpsertEntryAsync_CanceledClientTask_PropagatesCancellationAndToken()
    {
        var (storage, client) = CreateStorage();
        using var source = new CancellationTokenSource();
        source.Cancel();
        client.UpdateItemScripts.Enqueue((_, _) => Task.FromCanceled<UpdateItemResponse>(source.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.UpsertEntryAsync(
            TableName,
            new Dictionary<string, AttributeValue> { ["pk"] = new("one") },
            new Dictionary<string, AttributeValue> { ["field"] = new("value") }));

        Assert.Equal(source.Token, exception.CancellationToken);
        AssertCallOrder(client, "UpdateItem");
        Assert.Equal(CancellationToken.None, Assert.Single(client.UpdateItemCalls).CancellationToken);
        client.AssertAllScriptsConsumed();
    }

    private static (DynamoDBStorage Storage, RecordingAmazonDynamoDBClient Client) CreateStorage(
        int readCapacityUnits = DynamoDBStorage.DefaultReadCapacityUnits,
        int writeCapacityUnits = DynamoDBStorage.DefaultWriteCapacityUnits,
        bool useProvisionedThroughput = true,
        bool updateIfExists = true)
    {
        var storage = new DynamoDBStorage(
            NullLogger.Instance,
            "http://127.0.0.1:65535",
            accessKey: "dummy",
            secretKey: "dummy",
            readCapacityUnits: readCapacityUnits,
            writeCapacityUnits: writeCapacityUnits,
            useProvisionedThroughput: useProvisionedThroughput,
            updateIfExists: updateIfExists);
        var client = new RecordingAmazonDynamoDBClient();
        typeof(DynamoDBStorage)
            .GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(storage, client);
        return (storage, client);
    }

    private static async Task InvokeUpdateTableAsync(
        DynamoDBStorage storage,
        TableDescription table,
        List<AttributeDefinition> attributes,
        List<GlobalSecondaryIndex>? secondaryIndexes = null,
        string? ttlAttributeName = null,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(DynamoDBStorage).GetMethod("UpdateTableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var result = (ValueTask)method.Invoke(
                storage,
                [table, attributes, secondaryIndexes, ttlAttributeName, cancellationToken])!;
            await result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static async Task<TableDescription> InvokeTableIndexWaitOnStatusAsync(
        DynamoDBStorage storage,
        string indexName,
        IndexStatus whileStatus,
        IndexStatus? desiredStatus,
        int delay = 0,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(DynamoDBStorage).GetMethod("TableIndexWaitOnStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            return await (Task<TableDescription>)method.Invoke(
                storage,
                [TableName, indexName, whileStatus, desiredStatus, delay, cancellationToken])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static TableDescription Table(
        TableStatus status,
        long read = DynamoDBStorage.DefaultReadCapacityUnits,
        long write = DynamoDBStorage.DefaultWriteCapacityUnits,
        List<GlobalSecondaryIndexDescription>? indexes = null) =>
        new()
        {
            TableName = TableName,
            TableStatus = status,
            ProvisionedThroughput = new ProvisionedThroughputDescription
            {
                ReadCapacityUnits = read,
                WriteCapacityUnits = write
            },
            GlobalSecondaryIndexes = indexes
        };

    private static DescribeTableResponse Describe(TableDescription table) => new() { Table = table };

    private static GlobalSecondaryIndexDescription IndexDescription(string name, IndexStatus status) =>
        new()
        {
            IndexName = name,
            IndexStatus = status
        };

    private static GlobalSecondaryIndex RequestedIndex(string name) =>
        new()
        {
            IndexName = name,
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
            KeySchema = [new KeySchemaElement("gsi-pk", KeyType.HASH)]
        };

    private static void EnqueueDisabledTtl(RecordingAmazonDynamoDBClient client) =>
        EnqueueTtl(client, TimeToLiveStatus.DISABLED, null);

    private static void EnqueueTtl(RecordingAmazonDynamoDBClient client, TimeToLiveStatus status, string? attributeName) =>
        client.DescribeTimeToLiveScripts.Enqueue((_, _) => Task.FromResult(new DescribeTimeToLiveResponse
        {
            TimeToLiveDescription = new TimeToLiveDescription
            {
                TimeToLiveStatus = status,
                AttributeName = attributeName
            }
        }));

    private static void AssertCallOrder(RecordingAmazonDynamoDBClient client, params string[] expected) =>
        Assert.Equal(expected, client.Calls.Select(call => call.Operation));

    private static IEnumerable<T> ThrowOnEnumeration<T>(Exception exception)
    {
        throw exception;
#pragma warning disable CS0162 // Required to make this method a deferred iterator.
        yield break;
#pragma warning restore CS0162
    }

    private sealed class RecordingAmazonDynamoDBClient : AmazonDynamoDBClient
    {
        public RecordingAmazonDynamoDBClient()
            : base(
                new BasicAWSCredentials("dummy", "dummy"),
                new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:65535" })
        {
        }

        public List<ClientCall> Calls { get; } = [];
        public List<RecordedCall<DescribeTableRequest>> DescribeTableCalls { get; } = [];
        public List<RecordedCall<DescribeTimeToLiveRequest>> DescribeTimeToLiveCalls { get; } = [];
        public List<RecordedCall<UpdateTableRequest>> UpdateTableCalls { get; } = [];
        public List<RecordedCall<UpdateTimeToLiveRequest>> UpdateTimeToLiveCalls { get; } = [];
        public List<RecordedCall<UpdateItemRequest>> UpdateItemCalls { get; } = [];
        public List<RecordedCall<BatchWriteItemRequest>> BatchWriteItemCalls { get; } = [];
        public List<RecordedCall<TransactWriteItemsRequest>> TransactWriteItemsCalls { get; } = [];

        public Queue<Func<DescribeTableRequest, CancellationToken, Task<DescribeTableResponse>>> DescribeTableScripts { get; } = [];
        public Queue<Func<DescribeTimeToLiveRequest, CancellationToken, Task<DescribeTimeToLiveResponse>>> DescribeTimeToLiveScripts { get; } = [];
        public Queue<Func<UpdateTableRequest, CancellationToken, Task<UpdateTableResponse>>> UpdateTableScripts { get; } = [];
        public Queue<Func<UpdateTimeToLiveRequest, CancellationToken, Task<UpdateTimeToLiveResponse>>> UpdateTimeToLiveScripts { get; } = [];
        public Queue<Func<UpdateItemRequest, CancellationToken, Task<UpdateItemResponse>>> UpdateItemScripts { get; } = [];
        public Queue<Func<BatchWriteItemRequest, CancellationToken, Task<BatchWriteItemResponse>>> BatchWriteItemScripts { get; } = [];
        public Queue<Func<TransactWriteItemsRequest, CancellationToken, Task<TransactWriteItemsResponse>>> TransactWriteItemsScripts { get; } = [];

        public override Task<DescribeTableResponse> DescribeTableAsync(string tableName, CancellationToken cancellationToken = default) =>
            Record(
                "DescribeTable",
                new DescribeTableRequest { TableName = tableName },
                cancellationToken,
                DescribeTableCalls,
                DescribeTableScripts);

        public override Task<DescribeTimeToLiveResponse> DescribeTimeToLiveAsync(string tableName, CancellationToken cancellationToken = default) =>
            Record(
                "DescribeTimeToLive",
                new DescribeTimeToLiveRequest { TableName = tableName },
                cancellationToken,
                DescribeTimeToLiveCalls,
                DescribeTimeToLiveScripts);

        public override Task<UpdateTableResponse> UpdateTableAsync(UpdateTableRequest request, CancellationToken cancellationToken = default) =>
            Record("UpdateTable", request, cancellationToken, UpdateTableCalls, UpdateTableScripts);

        public override Task<UpdateTimeToLiveResponse> UpdateTimeToLiveAsync(UpdateTimeToLiveRequest request, CancellationToken cancellationToken = default) =>
            Record("UpdateTimeToLive", request, cancellationToken, UpdateTimeToLiveCalls, UpdateTimeToLiveScripts);

        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default) =>
            Record("UpdateItem", request, cancellationToken, UpdateItemCalls, UpdateItemScripts);

        public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default) =>
            Record("BatchWriteItem", request, cancellationToken, BatchWriteItemCalls, BatchWriteItemScripts);

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default) =>
            Record("TransactWriteItems", request, cancellationToken, TransactWriteItemsCalls, TransactWriteItemsScripts);

        public void AssertAllScriptsConsumed()
        {
            Assert.Empty(DescribeTableScripts);
            Assert.Empty(DescribeTimeToLiveScripts);
            Assert.Empty(UpdateTableScripts);
            Assert.Empty(UpdateTimeToLiveScripts);
            Assert.Empty(UpdateItemScripts);
            Assert.Empty(BatchWriteItemScripts);
            Assert.Empty(TransactWriteItemsScripts);
        }

        private Task<TResponse> Record<TRequest, TResponse>(
            string operation,
            TRequest request,
            CancellationToken cancellationToken,
            List<RecordedCall<TRequest>> typedCalls,
            Queue<Func<TRequest, CancellationToken, Task<TResponse>>> scripts)
        {
            Calls.Add(new ClientCall(operation, request!, cancellationToken));
            typedCalls.Add(new RecordedCall<TRequest>(request, cancellationToken));
            if (scripts.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected {operation} call.");
            }

            return scripts.Dequeue()(request, cancellationToken);
        }
    }

    private sealed record ClientCall(string Operation, object Request, CancellationToken CancellationToken);
    private sealed record RecordedCall<TRequest>(TRequest Request, CancellationToken CancellationToken);
}
