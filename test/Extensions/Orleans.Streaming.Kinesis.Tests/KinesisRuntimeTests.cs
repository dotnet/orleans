using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisRuntimeTests
{
    [Fact]
    public async Task GetPartitionIdsReadsAllPagesInDeterministicOrder()
    {
        var client = Substitute.For<IAmazonKinesis>();
        client.ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ListShardsRequest>();
                return Task.FromResult(request.NextToken is null
                    ? new ListShardsResponse
                    {
                        NextToken = "next",
                        Shards = [new Shard { ShardId = "shard-2" }, new Shard { ShardId = "shard-1" }],
                    }
                    : new ListShardsResponse
                    {
                        Shards = [new Shard { ShardId = "shard-3" }, new Shard { ShardId = "shard-1" }],
                    });
            });

        var result = await KinesisAdapterFactory.GetPartitionIdsAsync(
            client,
            "stream",
            TestContext.Current.CancellationToken);

        Assert.Equal(["shard-1", "shard-2", "shard-3"], result);
        await client.Received(2).ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPartitionIdsForwardsCancellationToken()
    {
        var client = Substitute.For<IAmazonKinesis>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        client.ListShardsAsync(Arg.Any<ListShardsRequest>(), cancellation.Token)
            .Returns(async _ =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                return null!;
            });

        var operation = KinesisAdapterFactory.GetPartitionIdsAsync(
            client,
            "stream",
            cancellation.Token);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await client.Received(1).ListShardsAsync(
            Arg.Any<ListShardsRequest>(),
            cancellation.Token);
    }

    [Fact]
    public void RegionIsInferredFromAwsServiceUrl()
    {
        var options = new KinesisStreamOptions
        {
            Service = "https://kinesis.us-west-2.amazonaws.com",
        };

        Assert.Equal("us-west-2", KinesisAdapterFactory.GetRegionName(options));
    }

    [Fact]
    public void ExplicitRegionOverridesServiceUrl()
    {
        var options = new KinesisStreamOptions
        {
            Service = "https://localhost:4566",
            Region = "eu-west-1",
        };

        Assert.Equal("eu-west-1", KinesisAdapterFactory.GetRegionName(options));
    }

    [Fact]
    public void QueueAdapterFactoryIsRewindable()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>();
        using var factory = new KinesisAdapterFactory(
            "Kinesis",
            new KinesisStreamOptions(),
            new SimpleQueueCacheOptions(),
            serializer,
            checkpointerFactory: null,
            NullLoggerFactory.Instance);

        Assert.True(factory.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, factory.Direction);
    }

    [Fact]
    public async Task ConcurrentCreateAdapterCallsInitializeOnce()
    {
        const int callerCount = 8;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var factory = new BlockingKinesisAdapterFactory(
            services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var invoked = new CountdownEvent(callerCount);
        var tasks = Enumerable.Range(0, callerCount).Select(async _ =>
        {
            await start.Task;
            var operation = factory.CreateAdapter(TestContext.Current.CancellationToken);
            invoked.Signal();
            return await operation;
        }).ToArray();

        start.SetResult();
        await Task.Run(
            () => invoked.Wait(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var partitionDiscoveryCount = factory.PartitionDiscoveryCount;
        factory.CompletePartitionDiscovery();

        var adapters = await Task.WhenAll(tasks);
        Assert.Equal(1, partitionDiscoveryCount);
        Assert.Equal(1, factory.PartitionDiscoveryCount);
        Assert.All(adapters, adapter => Assert.Same(factory, adapter));
    }

    [Fact]
    public async Task PooledReceiver_ReadsAndDisposesLifecycleCancellationOnShutdown()
    {
        var client = Substitute.For<IAmazonKinesis>();
        client.GetShardIteratorAsync(
                Arg.Any<GetShardIteratorRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new GetShardIteratorResponse { ShardIterator = "iterator" });
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetRecordsResponse
            {
                NextShardIterator = "iterator",
                Records = [],
            });
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>();
        var timeProvider = new FakeTimeProvider();
        var topologyMonitor = new KinesisShardTopologyMonitor(
            client,
            "stream",
            ["shard-1"],
            TimeSpan.FromMinutes(1),
            timeProvider,
            NullLogger<KinesisShardTopologyMonitor>.Instance);
        var receiver = new KinesisPooledAdapterReceiver(
            client,
            "stream",
            "shard-1",
            checkpointerFactory,
            new SimpleQueueCacheOptions(),
            serializer,
            NullLoggerFactory.Instance,
            topologyMonitor,
            TimeSpan.Zero,
            timeProvider);
        var lifecycleCancellationToken = receiver.LifecycleCancellationToken;
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        Assert.Empty(await receiver.GetQueueMessagesAsync(10, CancellationToken.None));

        await client.Received(1).GetRecordsAsync(
            Arg.Any<GetRecordsRequest>(),
            Arg.Any<CancellationToken>());
        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        Assert.True(lifecycleCancellationToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => _ = receiver.LifecycleCancellationToken);
    }

    private sealed class BlockingKinesisAdapterFactory(Serializer<KinesisBatchContainer.Body> serializer)
        : KinesisAdapterFactory(
            "Kinesis",
            new KinesisStreamOptions
            {
                StreamName = "stream",
                Service = "http://localhost:4566",
                AccessKey = "access-key",
                SecretKey = "secret-key",
            },
            new SimpleQueueCacheOptions(),
            serializer,
            checkpointerFactory: null,
            NullLoggerFactory.Instance)
    {
        private readonly TaskCompletionSource _partitionDiscovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _partitionDiscoveryCount;

        public int PartitionDiscoveryCount => Volatile.Read(ref _partitionDiscoveryCount);

        public void CompletePartitionDiscovery() => _partitionDiscovery.SetResult();

        internal override async Task<string[]> GetPartitionIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _partitionDiscoveryCount);
            await _partitionDiscovery.Task.WaitAsync(cancellationToken);
            return ["shard-1"];
        }
    }

    [Fact]
    public async Task InitialShardIteratorUsesTrimHorizonWhenNoCheckpointExists()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await client.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.TRIM_HORIZON
                && request.StartingSequenceNumber == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TopologyMonitorLatchesWhenShardSetChanges()
    {
        var client = Substitute.For<IAmazonKinesis>();
        client.ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListShardsResponse
            {
                Shards = [new Shard { ShardId = "shard-1" }, new Shard { ShardId = "shard-2" }],
            }));
        var monitor = new KinesisShardTopologyMonitor(
            client,
            "stream",
            ["shard-1"],
            TimeSpan.FromMinutes(1),
            new FakeTimeProvider(),
            NullLogger<KinesisShardTopologyMonitor>.Instance);

        Assert.False(await monitor.CheckTopology(force: true, TestContext.Current.CancellationToken));
        Assert.False(await monitor.CheckTopology(force: true, TestContext.Current.CancellationToken));
        await client.Received(1).ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverRenewsExpiredIteratorFromDurableCheckpoint()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty, "123");
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }),
                Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-2" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<GetRecordsResponse>(new ExpiredIteratorException("expired")),
                Task.FromResult(new GetRecordsResponse { NextShardIterator = "iterator-3", Records = [] }));
        var timeProvider = new FakeTimeProvider
        {
            AutoAdvanceAmount = TimeSpan.FromMilliseconds(200),
        };
        var receiver = CreateReceiver(client, checkpointerFactory, timeProvider);

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        var records = await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);

        Assert.Empty(records);
        await checkpointer.Received(2).Load(Arg.Any<CancellationToken>());
        await client.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.AFTER_SEQUENCE_NUMBER
                && request.StartingSequenceNumber == "123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverAssignsMonotonicallyIncreasingLocalOrdinalsAcrossReads()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new GetRecordsResponse
                {
                    NextShardIterator = "iterator-2",
                    Records = [
                        new Amazon.Kinesis.Model.Record { SequenceNumber = "10", Data = new MemoryStream() },
                        new Amazon.Kinesis.Model.Record { SequenceNumber = "20", Data = new MemoryStream() },
                    ],
                }),
                Task.FromResult(new GetRecordsResponse
                {
                    NextShardIterator = "iterator-3",
                    Records = [new Amazon.Kinesis.Model.Record { SequenceNumber = "30", Data = new MemoryStream() }],
                }));
        var timeProvider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(200) };
        var receiver = CreateReceiver(client, checkpointerFactory, timeProvider);
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        var firstBatch = (await receiver.GetQueueMessagesAsync(
            10,
            TestContext.Current.CancellationToken)).Cast<KinesisBatchContainer>().ToArray();
        var secondBatch = (await receiver.GetQueueMessagesAsync(
            10,
            TestContext.Current.CancellationToken)).Cast<KinesisBatchContainer>().ToArray();

        Assert.Equal([0L, 1L], firstBatch.Select(container => container.Token.SequenceNumber));
        Assert.Equal([2L], secondBatch.Select(container => container.Token.SequenceNumber));
    }

    [Fact]
    public async Task MessagesDeliveredCommitsNumericallyHighestShardSequence()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        var hugeButReadFirst = KinesisBatchContainer.FromKinesisRecord(
            null!,
            new Amazon.Kinesis.Model.Record { SequenceNumber = "170141183460469231731687303715884105727", Data = new MemoryStream() },
            sequenceId: 0);
        var smallButReadSecond = KinesisBatchContainer.FromKinesisRecord(
            null!,
            new Amazon.Kinesis.Model.Record { SequenceNumber = "42", Data = new MemoryStream() },
            sequenceId: 1);

        await receiver.MessagesDeliveredAsync(
            [hugeButReadFirst, smallButReadSecond],
            TestContext.Current.CancellationToken);

        checkpointer.Received(1).Update(
            "170141183460469231731687303715884105727",
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
        checkpointer.DidNotReceive().Update("42", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MessagesDeliveredWithEmptyListDoesNotUpdateCheckpointOrThrow()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await receiver.MessagesDeliveredAsync(
            Array.Empty<IBatchContainer>(),
            TestContext.Current.CancellationToken);

        checkpointer.DidNotReceive().Update(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverLimitsGetRecordsToFiveCallsPerSecond()
    {
        var timeProvider = new FakeTimeProvider();
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse { NextShardIterator = "iterator-1", Records = [] }));
        var receiver = CreateReceiver(client, checkpointerFactory, timeProvider);
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);
        var secondRead = receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);

        Assert.False(secondRead.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        await secondRead;
        await client.Received(2).GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverStopsPollingExhaustedShard()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse { NextShardIterator = null, Records = [] }));
        client.ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListShardsResponse { Shards = [new Shard { ShardId = "shard-1" }] }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        Assert.Empty(await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken));
        Assert.Empty(await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken));

        await client.Received(1).GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShutdownFlushesCheckpointAndDisposesClient()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        await checkpointer.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        client.Received(1).Dispose();
    }

    [Fact]
    public async Task ReceiverInitializationHonorsTimeout()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        var tokenObserved = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.Arg<CancellationToken>();
                tokenObserved.SetResult(cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null!;
            });
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());

        var operation = receiver.Initialize(TimeSpan.FromMilliseconds(100));
        var initializationToken = await tokenObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.True(initializationToken.CanBeCanceled);
        Assert.Equal(initializationToken, exception.CancellationToken);
        await client.DidNotReceive().GetShardIteratorAsync(
            Arg.Any<GetShardIteratorRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverReadRetriesAfterInitializationTimeout()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        var createCount = 0;
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }

                return checkpointer;
            });
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse
            {
                NextShardIterator = "iterator-1",
                Records = [],
            }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.Initialize(TimeSpan.FromMilliseconds(100)));
        var messages = await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
        Assert.Equal(2, createCount);
        await client.Received(1).GetRecordsAsync(
            Arg.Any<GetRecordsRequest>(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentInitializationRetriesWhenOwningCallerCancels()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        var firstAttemptStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var createCount = 0;
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    firstAttemptStarted.SetResult(token);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return checkpointer;
            });
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse
            {
                NextShardIterator = "iterator-1",
                Records = [],
            }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        using var ownerCancellation = new CancellationTokenSource();

        var owner = receiver.GetQueueMessagesAsync(10, ownerCancellation.Token);
        var unaffected = receiver.GetQueueMessagesAsync(10, CancellationToken.None);
        var firstAttemptToken = await firstAttemptStarted.Task;
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Empty(await unaffected);

        Assert.True(firstAttemptToken.IsCancellationRequested);
        Assert.Equal(2, createCount);
        await client.Received(1).GetShardIteratorAsync(
            Arg.Any<GetShardIteratorRequest>(),
            Arg.Any<CancellationToken>());
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentInitializationPropagatesProviderCancellationWhenOwnerIsActive()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        var createCount = 0;
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref createCount);
                return Task.FromException<IStreamQueueCheckpointer<string>>(
                    new OperationCanceledException("provider canceled independently"));
            });
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.GetQueueMessagesAsync(10, CancellationToken.None));

        Assert.Equal(1, createCount);
        await client.DidNotReceive().GetShardIteratorAsync(
            Arg.Any<GetShardIteratorRequest>(),
            Arg.Any<CancellationToken>());
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReceiverReadForwardsCancellationToken()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1", Arg.Any<CancellationToken>()).Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var tokenObserved = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.Arg<CancellationToken>();
                tokenObserved.SetResult(cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null!;
            });
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var operation = receiver.GetQueueMessagesAsync(10, cancellation.Token);
        Assert.Equal(cancellation.Token, await tokenObserved.Task.WaitAsync(TestContext.Current.CancellationToken));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static KinesisAdapterReceiver CreateReceiver(
        IAmazonKinesis client,
        IStreamQueueCheckpointerFactory checkpointerFactory,
        TimeProvider timeProvider)
    {
        var monitor = new KinesisShardTopologyMonitor(
            client,
            "stream",
            ["shard-1"],
            TimeSpan.FromMinutes(1),
            timeProvider,
            NullLogger<KinesisShardTopologyMonitor>.Instance);

        return new KinesisAdapterReceiver(
            client,
            "stream",
            "shard-1",
            checkpointerFactory,
            null!,
            NullLoggerFactory.Instance,
            monitor,
            TimeSpan.FromMilliseconds(200),
            timeProvider);
    }
}
