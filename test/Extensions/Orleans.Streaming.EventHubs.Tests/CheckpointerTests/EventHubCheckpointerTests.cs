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

    private EventHubAdapterReceiver CreateReceiver(TestCheckpointer checkpointer)
    {
        var settings = new EventHubPartitionSettings
        {
            Hub = new Orleans.Configuration.EventHubOptions(),
            Partition = "TestPartition",
            ReceiverOptions = new Orleans.Configuration.EventHubReceiverOptions()
        };

        var receiver = new EventHubAdapterReceiver(
            settings,
            cacheFactory: (_, _, _) => throw new NotImplementedException(),
            checkpointerFactory: _ => Task.FromResult<IStreamQueueCheckpointer<string>>(checkpointer),
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            monitor: new Orleans.Streaming.EventHubs.DefaultEventHubReceiverMonitor(
                new EventHubReceiverMonitorDimensions
                {
                    EventHubPartition = settings.Partition,
                    EventHubPath = settings.Hub.EventHubName,
                }),
            loadSheddingOptions: new Orleans.Configuration.LoadSheddingOptions(),
            environmentStatisticsProvider: new Orleans.Statistics.EnvironmentStatisticsProvider());

        // Initialize the receiver so the checkpointer is created.
        receiver.Initialize(TimeSpan.FromSeconds(5));

        return receiver;
    }

    [Fact, TestCategory("BVT")]
    public void SingleStream_SingleSubscription_CheckpointsDeliveredOffset()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub = MakeSubscriptionId();

        receiver.NotifyBatchDelivered(stream, sub, MakeToken(100));

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public void MultipleStreams_CheckpointsMinimumWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = CreateReceiver(checkpointer);

        var streamA = MakeStreamId("A");
        var streamB = MakeStreamId("B");
        var subA = MakeSubscriptionId();
        var subB = MakeSubscriptionId();

        // Stream A delivers at offset 200
        receiver.NotifyBatchDelivered(streamA, subA, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        // Stream B delivers at offset 95 — watermark should drop to 95
        receiver.NotifyBatchDelivered(streamB, subB, MakeToken(95));
        Assert.Equal("95", checkpointer.LastOffset);

        // Stream B advances to 210 — watermark should now be 200 (stream A's max)
        receiver.NotifyBatchDelivered(streamB, subB, MakeToken(210));
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public void MultipleSubscriptions_SameStream_CheckpointsMinimum()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub1 = MakeSubscriptionId();
        var sub2 = MakeSubscriptionId();

        // Sub1 delivers at offset 200
        receiver.NotifyBatchDelivered(stream, sub1, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        // Sub2 delivers at offset 50 — watermark drops to 50
        receiver.NotifyBatchDelivered(stream, sub2, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        // Sub2 catches up to 200 — watermark should now be 200
        receiver.NotifyBatchDelivered(stream, sub2, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        // Sub1 advances to 300 — watermark is 200 (sub2's max)
        receiver.NotifyBatchDelivered(stream, sub1, MakeToken(300));
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public void MultipleStreams_MultipleSubscriptions_InterleavedDelivery()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = CreateReceiver(checkpointer);

        var streamA = MakeStreamId("A");
        var streamB = MakeStreamId("B");
        var subA1 = MakeSubscriptionId();
        var subA2 = MakeSubscriptionId();
        var subB1 = MakeSubscriptionId();

        // Stream A sub1 delivers at offset 100
        receiver.NotifyBatchDelivered(streamA, subA1, MakeToken(100));
        Assert.Equal("100", checkpointer.LastOffset);

        // Stream A sub2 delivers at offset 80 — watermark drops to 80 (sub2 is behind)
        receiver.NotifyBatchDelivered(streamA, subA2, MakeToken(80));
        Assert.Equal("80", checkpointer.LastOffset);

        // Stream B sub1 delivers at offset 50 — watermark drops to 50
        receiver.NotifyBatchDelivered(streamB, subB1, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        // Stream B advances to 90 — watermark is now 80 (stream A sub2 is the laggard)
        receiver.NotifyBatchDelivered(streamB, subB1, MakeToken(90));
        Assert.Equal("80", checkpointer.LastOffset);

        // Stream A sub2 catches up to 120 — watermark is now 90 (stream B sub1)
        receiver.NotifyBatchDelivered(streamA, subA2, MakeToken(120));
        Assert.Equal("90", checkpointer.LastOffset);

        // Stream B advances to 150 — watermark is now 100 (stream A sub1)
        receiver.NotifyBatchDelivered(streamB, subB1, MakeToken(150));
        Assert.Equal("100", checkpointer.LastOffset);

        // Stream A sub1 advances to 200 — watermark is now 120 (stream A sub2)
        receiver.NotifyBatchDelivered(streamA, subA1, MakeToken(200));
        Assert.Equal("120", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public void PerSubscriptionMax_PreventsRegression()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = CreateReceiver(checkpointer);

        var stream = MakeStreamId("A");
        var sub = MakeSubscriptionId();

        receiver.NotifyBatchDelivered(stream, sub, MakeToken(100));
        Assert.Equal("100", checkpointer.LastOffset);

        // Same subscription reports an earlier offset (shouldn't happen normally,
        // but the per-subscription max should prevent regression)
        receiver.NotifyBatchDelivered(stream, sub, MakeToken(50));
        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public void NoNotifications_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        _ = CreateReceiver(checkpointer);

        Assert.Null(checkpointer.LastOffset);
        Assert.Equal(0, checkpointer.UpdateCount);
    }
}
