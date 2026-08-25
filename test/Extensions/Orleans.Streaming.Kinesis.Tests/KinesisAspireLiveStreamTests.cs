// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("Functional")]
[TestProvider("Kinesis")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisAspireLiveStreamTests(ITestOutputHelper output)
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task AspireGeneratedConfiguration_PublishesConsumesAndCheckpointsKinesisStream()
    {
        if (!KinesisTestConstants.IsAvailable || !KinesisTestConstants.IsDynamoDbAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Kinesis and DynamoDB services must both be configured for the Aspire live stream test.");
        }

        var suffix = Guid.NewGuid().ToString("N");
        var streamName = $"orleans-aspire-{suffix}";
        var pubSubTableName = $"OrleansKinesisAspirePubSub{suffix}";
        var checkpointTableName = $"OrleansKinesisAspireCheckpoint{suffix}";
        var streamMayExist = false;
        var pubSubTableMayExist = false;
        var checkpointTableMayExist = false;
        InProcessTestCluster? cluster = null;
        IAmazonDynamoDB? dynamoDb = null;
        ExceptionDispatchInfo? primaryFailure = null;
        List<Exception> cleanupFailures = [];

        try
        {
            await using var app = await KinesisAspireTestApp.CreateAsync();
            var topology = app.Topology;

            streamMayExist = true;
            await KinesisStreamTestResource.Create(streamName);
            var streamArn = await VerifyStreamAsync(streamName, topology.Stream.ShardCount);

            dynamoDb = CreateDynamoDbClient(checkpointTableName);
            pubSubTableMayExist = true;
            await CreateAndVerifyTableAsync(
                dynamoDb,
                pubSubTableName,
                topology.PubSubTable);
            checkpointTableMayExist = true;
            await CreateAndVerifyTableAsync(
                dynamoDb,
                checkpointTableName,
                topology.CheckpointTable);

            var siloConfiguration = CreateLiveConfiguration(
                await app.GetSiloEnvironmentAsync(),
                topology,
                streamName,
                streamArn,
                pubSubTableName,
                checkpointTableName,
                includeDynamoDbStorage: true);
            var clientConfiguration = CreateLiveConfiguration(
                await app.GetClientEnvironmentAsync(),
                topology,
                streamName,
                streamArn,
                pubSubTableName,
                checkpointTableName,
                includeDynamoDbStorage: false);
            AssertGeneratedShape(siloConfiguration, topology, streamName, pubSubTableName, checkpointTableName);

            var initialCheckpoints = await ReadCheckpointsAsync(
                dynamoDb,
                checkpointTableName,
                topology.ServiceId,
                topology.ProviderName);
            Assert.Empty(initialCheckpoints);

            var streamId = StreamId.Create("aspire", Guid.NewGuid());
            var firstPayload = $"first-{Guid.NewGuid():N}";
            cluster = BuildCluster(siloConfiguration, clientConfiguration);
            await cluster.DeployAsync();
            var firstDelivery = await PublishAndConsumeAsync(
                cluster,
                topology.ProviderName,
                streamId,
                firstPayload);

            Assert.Equal(streamId, firstDelivery.StreamId);
            Assert.Equal([firstPayload], firstDelivery.Payloads);

            await StopAndDisposeClusterAsync(cluster);
            cluster = null;

            var firstCheckpoint = Assert.Single(await WaitForCheckpointsAsync(
                dynamoDb,
                checkpointTableName,
                topology.ServiceId,
                topology.ProviderName,
                minimumVersion: 1));
            Assert.False(string.IsNullOrWhiteSpace(firstCheckpoint.Partition));
            Assert.False(string.IsNullOrWhiteSpace(firstCheckpoint.Sequence));
            Assert.True(firstCheckpoint.Version > 0);

            var secondPayload = $"second-{Guid.NewGuid():N}";
            cluster = BuildCluster(siloConfiguration, clientConfiguration);
            await cluster.DeployAsync();
            var secondDelivery = await PublishAndConsumeAsync(
                cluster,
                topology.ProviderName,
                streamId,
                secondPayload);

            Assert.Equal(streamId, secondDelivery.StreamId);
            Assert.Equal([secondPayload], secondDelivery.Payloads);

            await StopAndDisposeClusterAsync(cluster);
            cluster = null;

            var finalCheckpoints = await WaitForCheckpointsAsync(
                dynamoDb,
                checkpointTableName,
                topology.ServiceId,
                topology.ProviderName,
                minimumVersion: firstCheckpoint.Version + 1);
            var finalCheckpoint = Assert.Single(
                finalCheckpoints,
                value => value.Partition == firstCheckpoint.Partition);
            Assert.True(
                BigInteger.Parse(finalCheckpoint.Sequence, CultureInfo.InvariantCulture)
                    > BigInteger.Parse(firstCheckpoint.Sequence, CultureInfo.InvariantCulture),
                $"Expected checkpoint '{finalCheckpoint.Sequence}' to advance beyond '{firstCheckpoint.Sequence}'.");
            Assert.True(finalCheckpoint.Version > firstCheckpoint.Version);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (cluster is not null)
            {
                await CaptureCleanupFailureAsync(
                    () => StopAndDisposeClusterAsync(cluster),
                    "stop and dispose the Orleans cluster",
                    cleanupFailures);
            }

            if (dynamoDb is not null)
            {
                if (checkpointTableMayExist)
                {
                    await CaptureCleanupFailureAsync(
                        () => DeleteTableIfPresentAsync(dynamoDb, checkpointTableName),
                        $"delete checkpoint table '{checkpointTableName}'",
                        cleanupFailures);
                }

                if (pubSubTableMayExist)
                {
                    await CaptureCleanupFailureAsync(
                        () => DeleteTableIfPresentAsync(dynamoDb, pubSubTableName),
                        $"delete PubSub table '{pubSubTableName}'",
                        cleanupFailures);
                }

                dynamoDb.Dispose();
            }

            if (streamMayExist)
            {
                await CaptureCleanupFailureAsync(
                    () => KinesisStreamTestResource.Delete(streamName),
                    $"delete Kinesis stream '{streamName}'",
                    cleanupFailures);
            }
        }

        if (primaryFailure is not null)
        {
            foreach (var cleanupFailure in cleanupFailures)
            {
                output.WriteLine(
                    "Cleanup also failed without replacing the primary failure: {0}",
                    cleanupFailure);
            }

            primaryFailure.Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException("Live test cleanup failed.", cleanupFailures);
        }
    }

    private static InProcessTestCluster BuildCluster(
        IReadOnlyDictionary<string, string?> siloConfiguration,
        IReadOnlyDictionary<string, string?> clientConfiguration)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ClusterId = siloConfiguration["Orleans:ClusterId"]!;
        builder.Options.ServiceId = siloConfiguration["Orleans:ServiceId"]!;
        builder.ConfigureSiloHost((_, hostBuilder) =>
            hostBuilder.Configuration.AddInMemoryCollection(siloConfiguration));
        builder.ConfigureClientHost(hostBuilder =>
            hostBuilder.Configuration.AddInMemoryCollection(clientConfiguration));
        return builder.Build();
    }

    private static async Task<Delivery> PublishAndConsumeAsync(
        InProcessTestCluster cluster,
        string providerName,
        StreamId expectedStreamId,
        string payload)
    {
        var received = new ConcurrentQueue<string>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = cluster.Client
            .GetStreamProvider(providerName)
            .GetStream<string>(expectedStreamId);
        var subscription = await stream.SubscribeAsync((value, _) =>
        {
            received.Enqueue(value);
            if (value == payload)
            {
                delivered.TrySetResult();
            }

            return Task.CompletedTask;
        });

        try
        {
            await stream.OnNextAsync(payload);
            await delivered.Task.WaitAsync(OperationTimeout);
        }
        finally
        {
            await subscription.UnsubscribeAsync();
        }

        return new Delivery(stream.StreamId, received.ToArray());
    }

    private static Dictionary<string, string?> CreateLiveConfiguration(
        IReadOnlyDictionary<string, string?> generatedEnvironment,
        KinesisAspireTopologySpecification topology,
        string streamName,
        string streamArn,
        string pubSubTableName,
        string checkpointTableName,
        bool includeDynamoDbStorage)
    {
        var configuration = KinesisAspireTestApp
            .NormalizeConfiguration(generatedEnvironment)
            .ToDictionary(StringComparer.Ordinal);
        configuration.Remove("AWS_PROFILE");
        configuration.Remove("AWS:Profile");

        var streamingPrefix = $"Orleans:Streaming:{topology.ProviderName}";
        configuration[$"{streamingPrefix}:ConnectionString"] = KinesisTestConstants.ConnectionString;
        configuration[$"{streamingPrefix}:StreamName"] = streamName;
        configuration[$"{streamingPrefix}:Checkpoint:Service"] = KinesisTestConstants.DynamoDbService;
        configuration[$"{streamingPrefix}:Checkpoint:AccessKey"] = KinesisTestConstants.DynamoDbAccessKey;
        configuration[$"{streamingPrefix}:Checkpoint:SecretKey"] = KinesisTestConstants.DynamoDbSecretKey;
        configuration[$"{streamingPrefix}:Checkpoint:PersistInterval"] = "00:00:00.100";
        configuration[$"AWS:Resources:{topology.Stream.ResourceName}:StreamArn"] = streamArn;
        configuration[$"AWS:Resources:{topology.PubSubTable.ResourceName}:TableName"] = pubSubTableName;
        configuration[$"AWS:Resources:{topology.CheckpointTable.ResourceName}:TableName"] = checkpointTableName;

        if (includeDynamoDbStorage)
        {
            const string pubSubPrefix = "Orleans:GrainStorage:PubSubStore";
            configuration[$"{pubSubPrefix}:Service"] = KinesisTestConstants.DynamoDbService;
            configuration[$"{pubSubPrefix}:AccessKey"] = KinesisTestConstants.DynamoDbAccessKey;
            configuration[$"{pubSubPrefix}:SecretKey"] = KinesisTestConstants.DynamoDbSecretKey;
        }

        return configuration;
    }

    private static void AssertGeneratedShape(
        IReadOnlyDictionary<string, string?> configuration,
        KinesisAspireTopologySpecification topology,
        string streamName,
        string pubSubTableName,
        string checkpointTableName)
    {
        var streamingPrefix = $"Orleans:Streaming:{topology.ProviderName}";
        Assert.Equal("Kinesis", configuration[$"{streamingPrefix}:ProviderType"]);
        Assert.Equal(topology.Stream.ResourceName, configuration[$"{streamingPrefix}:ServiceKey"]);
        Assert.Equal(streamName, configuration[$"{streamingPrefix}:StreamName"]);
        Assert.Equal("DynamoDB", configuration[$"{streamingPrefix}:Checkpoint:Type"]);
        Assert.Equal(
            topology.CheckpointTable.ResourceName,
            configuration[$"{streamingPrefix}:Checkpoint:ServiceKey"]);
        Assert.Equal(
            pubSubTableName,
            configuration[$"AWS:Resources:{topology.PubSubTable.ResourceName}:TableName"]);
        Assert.Equal(
            checkpointTableName,
            configuration[$"AWS:Resources:{topology.CheckpointTable.ResourceName}:TableName"]);
        Assert.Equal("false", configuration[$"{streamingPrefix}:Checkpoint:UseProvisionedThroughput"]);
        Assert.Equal(
            "false",
            configuration["Orleans:GrainStorage:PubSubStore:UseProvisionedThroughput"]);
    }

    private static IAmazonDynamoDB CreateDynamoDbClient(string tableName)
        => DynamoDBStreamQueueCheckpointerFactory.CreateClient(
            new DynamoDBStreamQueueCheckpointerOptions
            {
                AccessKey = KinesisTestConstants.DynamoDbAccessKey,
                SecretKey = KinesisTestConstants.DynamoDbSecretKey,
                Service = KinesisTestConstants.DynamoDbService,
                TableName = tableName,
            });

    private static async Task<string> VerifyStreamAsync(string streamName, int expectedShardCount)
    {
        using var client = KinesisAdapterFactory.CreateClient(
            new KinesisStreamOptions
            {
                ConnectionString = KinesisTestConstants.ConnectionString,
                StreamName = streamName,
            });
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        var response = await client.DescribeStreamAsync(
            new DescribeStreamRequest { StreamName = streamName },
            cancellation.Token);

        Assert.Equal(streamName, response.StreamDescription.StreamName);
        Assert.Equal(StreamStatus.ACTIVE, response.StreamDescription.StreamStatus);
        Assert.Equal(expectedShardCount, response.StreamDescription.Shards.Count);
        Assert.Equal(4, response.StreamDescription.Shards.Count);
        Assert.Equal(24, response.StreamDescription.RetentionPeriodHours);
        if (response.StreamDescription.StreamModeDetails is { } modeDetails)
        {
            Assert.Equal(StreamMode.PROVISIONED, modeDetails.StreamMode);
        }
        else
        {
            var configuredOptions = new KinesisStreamOptions
            {
                ConnectionString = KinesisTestConstants.ConnectionString,
            };
            Assert.True(
                Uri.TryCreate(configuredOptions.Service, UriKind.Absolute, out var emulatorEndpoint)
                    && !emulatorEndpoint.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase),
                "Real AWS must report PROVISIONED stream mode; only an endpoint-backed emulator may omit mode metadata.");
        }
        Assert.False(string.IsNullOrWhiteSpace(response.StreamDescription.StreamARN));
        return response.StreamDescription.StreamARN;
    }

    private static async Task CreateAndVerifyTableAsync(
        IAmazonDynamoDB client,
        string tableName,
        DynamoDbTableSpecification specification)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        await client.CreateTableAsync(
            new CreateTableRequest
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions =
                [
                    new(specification.PartitionKey.AttributeName, ScalarAttributeType.S),
                    new(specification.SortKey.AttributeName, ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new(specification.PartitionKey.AttributeName, KeyType.HASH),
                    new(specification.SortKey.AttributeName, KeyType.RANGE),
                ],
            },
            cancellation.Token);

        TableDescription table;
        while (true)
        {
            var response = await client.DescribeTableAsync(tableName, cancellation.Token);
            table = response.Table;
            if (table.TableStatus == TableStatus.ACTIVE)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
        }

        Assert.Equal(tableName, table.TableName);
        Assert.Equal(BillingMode.PAY_PER_REQUEST, table.BillingModeSummary?.BillingMode);
        Assert.Contains(
            table.KeySchema,
            key => key.AttributeName == specification.PartitionKey.AttributeName
                && key.KeyType == KeyType.HASH);
        Assert.Contains(
            table.KeySchema,
            key => key.AttributeName == specification.SortKey.AttributeName
                && key.KeyType == KeyType.RANGE);
        Assert.Contains(
            table.AttributeDefinitions,
            attribute => attribute.AttributeName == specification.PartitionKey.AttributeName
                && attribute.AttributeType == ScalarAttributeType.S);
        Assert.Contains(
            table.AttributeDefinitions,
            attribute => attribute.AttributeName == specification.SortKey.AttributeName
                && attribute.AttributeType == ScalarAttributeType.S);
    }

    private static async Task<IReadOnlyList<CheckpointRecord>> WaitForCheckpointsAsync(
        IAmazonDynamoDB client,
        string tableName,
        string serviceId,
        string providerName,
        long minimumVersion)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        while (true)
        {
            var checkpoints = await ReadCheckpointsAsync(
                client,
                tableName,
                serviceId,
                providerName,
                cancellation.Token);
            if (checkpoints.Any(value => value.Version >= minimumVersion))
            {
                return checkpoints;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
        }
    }

    private static async Task<IReadOnlyList<CheckpointRecord>> ReadCheckpointsAsync(
        IAmazonDynamoDB client,
        string tableName,
        string serviceId,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        var checkpointNamespace = DynamoDBStreamCheckpointStore.FormatNamespace(serviceId, providerName);
        var result = new List<CheckpointRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await client.ScanAsync(
                new ScanRequest
                {
                    TableName = tableName,
                    ExclusiveStartKey = lastKey,
                    FilterExpression = "#namespace = :namespace",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#namespace"] = DynamoDBStreamCheckpointStore.NamespaceAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":namespace"] = new(checkpointNamespace),
                    },
                },
                cancellationToken);
            foreach (var item in response.Items)
            {
                if (item.TryGetValue(DynamoDBStreamCheckpointStore.PartitionAttribute, out var partition)
                    && item.TryGetValue(DynamoDBStreamCheckpointStore.CheckpointAttribute, out var checkpoint)
                    && item.TryGetValue(DynamoDBStreamCheckpointStore.VersionAttribute, out var version)
                    && long.TryParse(
                        version.N,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedVersion))
                {
                    result.Add(new CheckpointRecord(partition.S, checkpoint.S, parsedVersion));
                }
            }

            lastKey = response.LastEvaluatedKey;
        }
        while (lastKey is { Count: > 0 });

        return result;
    }

    private static async Task StopAndDisposeClusterAsync(InProcessTestCluster cluster)
    {
        ExceptionDispatchInfo? stopFailure = null;
        try
        {
            await cluster.StopAllSilosAsync();
        }
        catch (Exception exception)
        {
            stopFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await cluster.DisposeAsync();
        }
        catch when (stopFailure is not null)
        {
        }

        stopFailure?.Throw();
    }

    private static async Task DeleteTableIfPresentAsync(
        IAmazonDynamoDB client,
        string tableName)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
        try
        {
            await client.DeleteTableAsync(
                new DeleteTableRequest { TableName = tableName },
                cancellation.Token);
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
        {
            return;
        }

        while (true)
        {
            try
            {
                await client.DescribeTableAsync(tableName, cancellation.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                return;
            }
        }
    }

    private static async Task CaptureCleanupFailureAsync(
        Func<Task> cleanup,
        string operation,
        ICollection<Exception> failures)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException($"Failed to {operation}.", exception));
        }
    }

    private sealed record Delivery(StreamId StreamId, string[] Payloads);

    private sealed record CheckpointRecord(string Partition, string Sequence, long Version);
}
