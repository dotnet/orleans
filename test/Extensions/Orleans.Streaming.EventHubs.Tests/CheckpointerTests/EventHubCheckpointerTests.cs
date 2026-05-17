using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Streams;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

/// <summary>
/// Tests for EventHub delivery-based checkpointing and low-watermark tracking.
/// </summary>
[TestCategory("EventHub"), TestCategory("Streaming")]
public class EventHubCheckpointerTests
{
    /// <summary>
    /// A test checkpointer that records all updates for verification.
    /// </summary>
    private class TestCheckpointer : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => true;
        public string LastOffset { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<string> Load() => Task.FromResult("-1");

        public void Update(string offset, DateTime utcNow)
        {
            LastOffset = offset;
            UpdateCount++;
        }

        public Task FlushAsync()
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestEventHubQueueCache : IEventHubQueueCache
    {
        public int GetMaxAddCount() => 1_000;

        public List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc) => [];

        public object GetCursor(StreamId streamId, StreamSequenceToken sequenceToken) => new();

        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            message = null;
            return false;
        }

        public void AddCachePressureMonitor(ICachePressureMonitor monitor)
        {
        }

        public void SignalPurge()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestEventHubReceiver : IEventHubReceiver
    {
        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            return Task.FromResult<IEnumerable<EventData>>([]);
        }

        public Task CloseAsync()
        {
            return Task.CompletedTask;
        }
    }

    private static EventHubSequenceToken MakeToken(long offset, long sequenceNumber = 0)
    {
        return new EventHubSequenceToken(offset.ToString(), sequenceNumber, 0);
    }

    private static StreamId MakeStreamId(string name)
    {
        return StreamId.Create("TestNamespace", name);
    }

    private static GuidId MakeSubscriptionId()
    {
        return GuidId.GetGuidId(Guid.NewGuid());
    }

    private static async Task<EventHubAdapterReceiver> CreateReceiver(TestCheckpointer checkpointer)
    {
        var settings = new EventHubPartitionSettings
        {
            Hub = new Orleans.Configuration.EventHubOptions(),
            Partition = "TestPartition",
            ReceiverOptions = new Orleans.Configuration.EventHubReceiverOptions()
        };

        var receiver = new EventHubAdapterReceiver(
            settings,
            cacheFactory: (_, _, _) => new TestEventHubQueueCache(),
            checkpointerFactory: _ => Task.FromResult<IStreamQueueCheckpointer<string>>(checkpointer),
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            monitor: new Orleans.Streaming.EventHubs.DefaultEventHubReceiverMonitor(
                new EventHubReceiverMonitorDimensions
                {
                    EventHubPartition = settings.Partition,
                    EventHubPath = settings.Hub.EventHubName,
                }),
            loadSheddingOptions: new Orleans.Configuration.LoadSheddingOptions(),
            environmentStatisticsProvider: new Orleans.Statistics.EnvironmentStatisticsProvider(),
            eventHubReceiverFactory: (_, _, _) => new TestEventHubReceiver());

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        return receiver;
    }

    [Fact, TestCategory("BVT")]
    public async Task SingleStream_SingleSubscription_CheckpointsProcessedOffset()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub = MakeSubscriptionId();

        receiver.NotifySubscriptionAdded(stream, sub, null);
        Assert.Null(checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(stream, sub, MakeToken(100));

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task MultipleStreams_CheckpointsMinimumWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var streamA = MakeStreamId("A");
        var streamB = MakeStreamId("B");
        var subA = MakeSubscriptionId();
        var subB = MakeSubscriptionId();

        receiver.NotifySubscriptionAdded(streamA, subA, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        receiver.NotifySubscriptionAdded(streamB, subB, MakeToken(95));
        Assert.Equal("95", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamB, subB, MakeToken(210));
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task PendingRegistration_BlocksCheckpointUntilSubscriptionsAreKnown()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub1 = MakeSubscriptionId();
        var sub2 = MakeSubscriptionId();

        receiver.NotifyStreamRegistrationStarted(stream);
        receiver.NotifySubscriptionAdded(stream, sub1, MakeToken(200));
        receiver.NotifySubscriptionAdded(stream, sub2, MakeToken(50));
        Assert.Null(checkpointer.LastOffset);

        receiver.NotifyStreamRegistrationCompleted(stream);
        Assert.Equal("50", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(stream, sub2, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(stream, sub1, MakeToken(300));
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task RemovedSubscription_NoLongerHoldsWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub1 = MakeSubscriptionId();
        var sub2 = MakeSubscriptionId();

        receiver.NotifySubscriptionAdded(stream, sub1, null);
        receiver.NotifySubscriptionAdded(stream, sub2, null);

        receiver.NotifyBatchProcessed(stream, sub1, MakeToken(200));
        Assert.Null(checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(stream, sub2, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        receiver.NotifySubscriptionRemoved(stream, sub2);
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task MultipleStreams_MultipleSubscriptions_InterleavedDelivery()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var streamA = MakeStreamId("A");
        var streamB = MakeStreamId("B");
        var subA1 = MakeSubscriptionId();
        var subA2 = MakeSubscriptionId();
        var subB1 = MakeSubscriptionId();

        receiver.NotifySubscriptionAdded(streamA, subA1, null);
        receiver.NotifySubscriptionAdded(streamA, subA2, null);
        receiver.NotifySubscriptionAdded(streamB, subB1, null);

        receiver.NotifyBatchProcessed(streamA, subA1, MakeToken(100));
        Assert.Null(checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamA, subA2, MakeToken(80));
        Assert.Null(checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamB, subB1, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamB, subB1, MakeToken(90));
        Assert.Equal("80", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamA, subA2, MakeToken(120));
        Assert.Equal("90", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamB, subB1, MakeToken(150));
        Assert.Equal("100", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(streamA, subA1, MakeToken(200));
        Assert.Equal("120", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task PerSubscriptionMax_PreventsRegression()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub = MakeSubscriptionId();

        receiver.NotifySubscriptionAdded(stream, sub, null);
        receiver.NotifyBatchProcessed(stream, sub, MakeToken(100));
        Assert.Equal("100", checkpointer.LastOffset);

        receiver.NotifyBatchProcessed(stream, sub, MakeToken(50));
        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task NoNotifications_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        _ = await CreateReceiver(checkpointer);

        Assert.Null(checkpointer.LastOffset);
        Assert.Equal(0, checkpointer.UpdateCount);
    }
}
