using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

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

        var result = await KinesisAdapterFactory.GetPartitionIdsAsync(client, "stream");

        Assert.Equal(["shard-1", "shard-2", "shard-3"], result);
        await client.Received(2).ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>());
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

        Assert.False(await monitor.CheckTopology(force: true));
        Assert.False(await monitor.CheckTopology(force: true));
        await client.Received(1).ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverRenewsExpiredIteratorFromDurableCheckpoint()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load().Returns(string.Empty, "123");
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1").Returns(checkpointer);
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
        var records = await receiver.GetQueueMessagesAsync(10);

        Assert.Empty(records);
        await checkpointer.Received(2).Load();
        await client.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.AFTER_SEQUENCE_NUMBER
                && request.StartingSequenceNumber == "123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiverLimitsGetRecordsToFiveCallsPerSecond()
    {
        var timeProvider = new FakeTimeProvider();
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load().Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1").Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse { NextShardIterator = "iterator-1", Records = [] }));
        var receiver = CreateReceiver(client, checkpointerFactory, timeProvider);
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await receiver.GetQueueMessagesAsync(10);
        var secondRead = receiver.GetQueueMessagesAsync(10);

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
        checkpointer.Load().Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1").Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        client.GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetRecordsResponse { NextShardIterator = null, Records = [] }));
        client.ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListShardsResponse { Shards = [new Shard { ShardId = "shard-1" }] }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        Assert.Empty(await receiver.GetQueueMessagesAsync(10));
        Assert.Empty(await receiver.GetQueueMessagesAsync(10));

        await client.Received(1).GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShutdownFlushesCheckpointAndDisposesClient()
    {
        var client = Substitute.For<IAmazonKinesis>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.Load().Returns(string.Empty);
        var checkpointerFactory = Substitute.For<IStreamQueueCheckpointerFactory>();
        checkpointerFactory.Create("shard-1").Returns(checkpointer);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetShardIteratorResponse { ShardIterator = "iterator-1" }));
        var receiver = CreateReceiver(client, checkpointerFactory, new FakeTimeProvider());
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        await checkpointer.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        client.Received(1).Dispose();
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
