using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class DynamoDBStreamQueueCheckpointerTests : StreamQueueCheckpointerTests
{
    protected override OffsetRegressionPolicy RegressionPolicy => OffsetRegressionPolicy.Ignore;

    protected override Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store)
    {
        var checkpointer = new DynamoDBStreamQueueCheckpointer(
            new TestCheckpointStore(store),
            new DynamoDBStreamQueueCheckpointerOptions
            {
                PersistInterval = PersistInterval,
            });
        return Task.FromResult<IStreamQueueCheckpointer<string>>(checkpointer);
    }

    private sealed class TestCheckpointStore(ControllableCheckpointStore store) : IDynamoDBStreamCheckpointStore
    {
        public ValueTask<string> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(store.Load());
        }

        public async ValueTask<string> Update(
            string checkpoint,
            string expectedCheckpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.Write(checkpoint).ConfigureAwait(false);
            return checkpoint;
        }
    }
}

[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class DynamoDBStreamCheckpointStoreTests
{
    [Fact]
    public async Task UpdatePersistsArbitrarySizeSequenceNumberAsString()
    {
        const string checkpoint = "123456789012345678901234567890123456789";
        var client = Substitute.For<IAmazonDynamoDB>();
        client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetItemResponse()));
        PutItemRequest? write = null;
        client.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                write = call.Arg<PutItemRequest>();
                return Task.FromResult(new PutItemResponse());
            });
        var store = CreateStore(client);

        await store.Update(checkpoint, string.Empty, CancellationToken.None);

        Assert.NotNull(write);
        Assert.Equal(checkpoint, write.Item[DynamoDBStreamCheckpointStore.CheckpointAttribute].S);
        Assert.Equal("1", write.Item[DynamoDBStreamCheckpointStore.VersionAttribute].N);
        Assert.Equal(
            "attribute_not_exists(#namespace) AND attribute_not_exists(#partition)",
            write.ConditionExpression);
        Assert.Equal(2, write.ExpressionAttributeNames.Count);
        Assert.DoesNotContain("#version", write.ExpressionAttributeNames);
    }

    [Fact]
    public async Task ConditionalConflictWithNewerCheckpointDoesNotOverwriteIt()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var readCount = 0;
        client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                readCount++ == 0 ? new GetItemResponse() : CreateReadResponse("20", 7)));
        client.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PutItemResponse>(
                new ConditionalCheckFailedException("stale checkpoint")));
        var store = CreateStore(client);

        Assert.Equal("20", await store.Update("10", string.Empty, CancellationToken.None));

        await client.Received(1).PutItemAsync(
            Arg.Any<PutItemRequest>(),
            Arg.Any<CancellationToken>());
        Assert.Equal("20", await store.Load(CancellationToken.None));
    }

    [Fact]
    public async Task ConditionalConflictWithOlderCheckpointAllowsRetryUsingItsVersion()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var readCount = 0;
        client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(readCount++ switch
            {
                0 => new GetItemResponse(),
                1 => CreateReadResponse("20", 7),
                _ => CreateReadResponse("30", 8),
            }));
        var writes = new List<PutItemRequest>();
        client.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                writes.Add(call.Arg<PutItemRequest>());
                return writes.Count == 1
                    ? Task.FromException<PutItemResponse>(
                        new ConditionalCheckFailedException("stale checkpoint"))
                    : Task.FromResult(new PutItemResponse());
            });
        var store = CreateStore(client);

        Assert.Equal("20", await store.Update("30", string.Empty, CancellationToken.None));
        Assert.Equal("30", await store.Update("30", "20", CancellationToken.None));

        Assert.Equal(2, writes.Count);
        Assert.Equal("#version = :expectedVersion", writes[1].ConditionExpression);
        Assert.Single(writes[1].ExpressionAttributeNames);
        Assert.Equal(
            DynamoDBStreamCheckpointStore.VersionAttribute,
            writes[1].ExpressionAttributeNames["#version"]);
        Assert.Equal("7", writes[1].ExpressionAttributeValues[":expectedVersion"].N);
        Assert.Equal("8", writes[1].Item[DynamoDBStreamCheckpointStore.VersionAttribute].N);
        Assert.Equal("30", await store.Load(CancellationToken.None));
    }

    [Fact]
    public async Task ExpectedCheckpointMismatchReturnsPersistedCheckpointWithoutWriting()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateReadResponse("20", 7)));
        var store = CreateStore(client);

        var result = await store.Update("30", "10", CancellationToken.None);

        Assert.Equal("20", result);
        await client.DidNotReceive().PutItemAsync(
            Arg.Any<PutItemRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWithCanceledTokenDoesNotAccessDynamoDB()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var store = CreateStore(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.Update("10", string.Empty, cancellation.Token).AsTask());

        await client.DidNotReceive().GetItemAsync(
            Arg.Any<GetItemRequest>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().PutItemAsync(
            Arg.Any<PutItemRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FactoryCreateWithCanceledTokenDoesNotAccessDynamoDB()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        using var factory = new DynamoDBStreamQueueCheckpointerFactory(
            "provider",
            new DynamoDBStreamQueueCheckpointerOptions { TableName = "checkpoints" },
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            NullLoggerFactory.Instance,
            client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.Create("shard-1", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await client.DidNotReceive().DescribeTableAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeTableCreatesOnDemandTableWithExpectedSchema()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
        };
        client.DescribeTableAsync(options.TableName, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DescribeTableResponse>(
                new ResourceNotFoundException("missing")));
        CreateTableRequest? create = null;
        client.CreateTableAsync(Arg.Any<CreateTableRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                create = call.Arg<CreateTableRequest>();
                return Task.FromResult(new CreateTableResponse
                {
                    TableDescription = CreateTableDescription(options.TableName),
                });
            });

        await DynamoDBStreamCheckpointStore.InitializeTable(
            client,
            options,
            NullLogger<DynamoDBStreamCheckpointStore>.Instance);

        Assert.NotNull(create);
        Assert.Equal(BillingMode.PAY_PER_REQUEST, create.BillingMode);
        Assert.Null(create.ProvisionedThroughput);
        Assert.Contains(
            create.KeySchema,
            key => key.AttributeName == DynamoDBStreamCheckpointStore.NamespaceAttribute
                && key.KeyType == KeyType.HASH);
        Assert.Contains(
            create.KeySchema,
            key => key.AttributeName == DynamoDBStreamCheckpointStore.PartitionAttribute
                && key.KeyType == KeyType.RANGE);
    }

    [Fact]
    public async Task InitializeTableRejectsUnexpectedKeySchema()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
        };
        client.DescribeTableAsync(options.TableName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DescribeTableResponse
            {
                Table = new TableDescription
                {
                    TableName = options.TableName,
                    TableStatus = TableStatus.ACTIVE,
                    AttributeDefinitions = [new("Unexpected", ScalarAttributeType.S)],
                    KeySchema = [new("Unexpected", KeyType.HASH)],
                },
            }));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => DynamoDBStreamCheckpointStore.InitializeTable(
                client,
                options,
                NullLogger<DynamoDBStreamCheckpointStore>.Instance));

        Assert.Contains(options.TableName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeTableRetriesTransientNotFoundAfterCreation()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
            InitializationTimeout = TimeSpan.FromSeconds(5),
        };
        var describeCount = 0;
        client.DescribeTableAsync(options.TableName, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                describeCount++;
                return describeCount < 3
                    ? Task.FromException<DescribeTableResponse>(
                        new ResourceNotFoundException("not visible yet"))
                    : Task.FromResult(new DescribeTableResponse
                    {
                        Table = CreateTableDescription(options.TableName),
                    });
            });
        client.CreateTableAsync(Arg.Any<CreateTableRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateTableResponse
            {
                TableDescription = new TableDescription
                {
                    TableName = options.TableName,
                    TableStatus = TableStatus.CREATING,
                },
            }));

        await DynamoDBStreamCheckpointStore.InitializeTable(
            client,
            options,
            NullLogger<DynamoDBStreamCheckpointStore>.Instance);

        Assert.Equal(3, describeCount);
    }

    [Fact]
    public async Task InitializeTableReportsTimeoutWithTableContext()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
            InitializationTimeout = TimeSpan.FromMilliseconds(1),
        };
        client.DescribeTableAsync(options.TableName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DescribeTableResponse
            {
                Table = new TableDescription
                {
                    TableName = options.TableName,
                    TableStatus = TableStatus.CREATING,
                },
            }));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => DynamoDBStreamCheckpointStore.InitializeTable(
                client,
                options,
                NullLogger<DynamoDBStreamCheckpointStore>.Instance));

        Assert.Contains(options.TableName, exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task InitializeTablePropagatesCallerCancellation()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
            InitializationTimeout = TimeSpan.FromMinutes(1),
        };
        client.DescribeTableAsync(options.TableName, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return null!;
            });
        using var cancellation = new CancellationTokenSource();

        var operation = DynamoDBStreamCheckpointStore.InitializeTable(
            client,
            options,
            NullLogger<DynamoDBStreamCheckpointStore>.Instance,
            cancellation.Token);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task FactoryRetriesInitializationAfterTransientFailure()
    {
        var client = Substitute.For<IAmazonDynamoDB>();
        var describeCount = 0;
        client.DescribeTableAsync("checkpoints", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (describeCount++ == 0)
                {
                    return Task.FromException<DescribeTableResponse>(
                        new AmazonDynamoDBException("transient failure"));
                }

                return Task.FromResult(new DescribeTableResponse
                {
                    Table = CreateTableDescription("checkpoints"),
                });
            });
        client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetItemResponse()));
        var options = new DynamoDBStreamQueueCheckpointerOptions
        {
            TableName = "checkpoints",
        };
        using var factory = new DynamoDBStreamQueueCheckpointerFactory(
            "provider",
            options,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            NullLoggerFactory.Instance,
            client);

        await Assert.ThrowsAsync<AmazonDynamoDBException>(() => factory.Create("shard-1"));
        var checkpointer = await factory.Create("shard-1");

        Assert.False(checkpointer.CheckpointExists);
        await client.Received(2).DescribeTableAsync(
            options.TableName,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void NamespaceEncodingPreventsAmbiguousServiceAndProviderPairs()
    {
        var first = DynamoDBStreamCheckpointStore.FormatNamespace("service:a", "provider");
        var second = DynamoDBStreamCheckpointStore.FormatNamespace("service", "a:provider");

        Assert.NotEqual(first, second);
    }

    private static DynamoDBStreamCheckpointStore CreateStore(
        IAmazonDynamoDB client)
        => new(client, "checkpoints", "service", "provider", "shard-1");

    private static GetItemResponse CreateReadResponse(string checkpoint, long version)
        => new()
        {
            Item = new Dictionary<string, AttributeValue>
            {
                [DynamoDBStreamCheckpointStore.NamespaceAttribute] = new("namespace"),
                [DynamoDBStreamCheckpointStore.PartitionAttribute] = new("shard-1"),
                [DynamoDBStreamCheckpointStore.CheckpointAttribute] = new(checkpoint),
                [DynamoDBStreamCheckpointStore.VersionAttribute] = new()
                {
                    N = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
        };

    private static TableDescription CreateTableDescription(string tableName)
        => new()
        {
            TableName = tableName,
            TableStatus = TableStatus.ACTIVE,
            AttributeDefinitions =
            [
                new(DynamoDBStreamCheckpointStore.NamespaceAttribute, ScalarAttributeType.S),
                new(DynamoDBStreamCheckpointStore.PartitionAttribute, ScalarAttributeType.S),
            ],
            KeySchema =
            [
                new(DynamoDBStreamCheckpointStore.NamespaceAttribute, KeyType.HASH),
                new(DynamoDBStreamCheckpointStore.PartitionAttribute, KeyType.RANGE),
            ],
        };
}

[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class DynamoDBStreamQueueCheckpointerIntegrationTests
{
    private static readonly DateTime TestTimeUtc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [SkippableFact]
    public async Task CheckpointsAreDurableAndIsolatedByProviderAndPartition()
    {
        KinesisTestConstants.CheckDynamoDbPreconditionsOrThrow();
        var tableName = CreateTableName();
        var options = CreateOptions(tableName);

        try
        {
            using var factory = CreateFactory("provider-a", "service-a", options);
            using var otherProviderFactory = CreateFactory("provider-b", "service-a", options);
            var first = await factory.Create("shard-1");
            var otherPartition = await factory.Create("shard-2");
            var otherProvider = await otherProviderFactory.Create("shard-1");

            first.Update("123456789012345678901234567890123456789", TestTimeUtc);
            otherPartition.Update("20", TestTimeUtc);
            otherProvider.Update("30", TestTimeUtc);
            await first.FlushAsync(CancellationToken.None);
            await otherPartition.FlushAsync(CancellationToken.None);
            await otherProvider.FlushAsync(CancellationToken.None);

            using var reloadedFactory = CreateFactory("provider-a", "service-a", options);
            using var reloadedOtherProviderFactory = CreateFactory("provider-b", "service-a", options);
            Assert.Equal(
                "123456789012345678901234567890123456789",
                await (await reloadedFactory.Create("shard-1")).Load());
            Assert.Equal("20", await (await reloadedFactory.Create("shard-2")).Load());
            Assert.Equal("30", await (await reloadedOtherProviderFactory.Create("shard-1")).Load());
        }
        finally
        {
            await DeleteTable(tableName, options);
        }
    }

    [SkippableFact]
    public async Task StaleWriterCannotMoveCheckpointBackward()
    {
        KinesisTestConstants.CheckDynamoDbPreconditionsOrThrow();
        var tableName = CreateTableName();
        var options = CreateOptions(tableName);

        try
        {
            using var firstFactory = CreateFactory("provider", "service", options);
            using var staleFactory = CreateFactory("provider", "service", options);
            var first = await firstFactory.Create("shard-1");
            var stale = await staleFactory.Create("shard-1");

            first.Update("20", TestTimeUtc);
            await first.FlushAsync(CancellationToken.None);
            stale.Update("10", TestTimeUtc);
            await stale.FlushAsync(CancellationToken.None);

            using var reloadedFactory = CreateFactory("provider", "service", options);
            Assert.Equal("20", await (await reloadedFactory.Create("shard-1")).Load());

            stale.Update("30", TestTimeUtc + options.PersistInterval);
            await stale.FlushAsync(CancellationToken.None);
            using var finalFactory = CreateFactory("provider", "service", options);
            Assert.Equal("30", await (await finalFactory.Create("shard-1")).Load());
        }
        finally
        {
            await DeleteTable(tableName, options);
        }
    }

    [SkippableFact]
    public async Task MissingTableFailsWhenCreationIsDisabled()
    {
        KinesisTestConstants.CheckDynamoDbPreconditionsOrThrow();
        var options = CreateOptions(CreateTableName());
        options.CreateIfNotExists = false;
        using var factory = CreateFactory("provider", "service", options);

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => factory.Create("shard-1"));

        Assert.Contains(options.TableName, exception.Message, StringComparison.Ordinal);
    }

    private static DynamoDBStreamQueueCheckpointerFactory CreateFactory(
        string providerName,
        string serviceId,
        DynamoDBStreamQueueCheckpointerOptions options)
        => new(
            providerName,
            options,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = serviceId }),
            NullLoggerFactory.Instance);

    private static DynamoDBStreamQueueCheckpointerOptions CreateOptions(string tableName)
        => new()
        {
            AccessKey = KinesisTestConstants.DynamoDbAccessKey,
            SecretKey = KinesisTestConstants.DynamoDbSecretKey,
            Service = KinesisTestConstants.DynamoDbService,
            TableName = tableName,
            InitializationTimeout = TimeSpan.FromSeconds(30),
            PersistInterval = TimeSpan.FromMilliseconds(1),
        };

    private static string CreateTableName() => $"OrleansKinesisCheckpoint{Guid.NewGuid():N}";

    private static async Task DeleteTable(
        string tableName,
        DynamoDBStreamQueueCheckpointerOptions options)
    {
        using var client = DynamoDBStreamQueueCheckpointerFactory.CreateClient(options);
        try
        {
            _ = await client.DeleteTableAsync(
                new DeleteTableRequest { TableName = tableName },
                CancellationToken.None);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
