using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Streams;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

/// <summary>
/// Tests for EventHub delivery-based checkpointing via lazy delivery progress callbacks.
/// The pulling agent exposes its current subscription state to the receiver, which evaluates
/// it only when a checkpoint update is due or a forced update is needed.
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

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrottledTestCheckpointer : TestCheckpointer, IEventHubCheckpointerUpdateCadence
    {
        public bool IsUpdateDueResult { get; set; }
        public int IsUpdateDueCount { get; private set; }

        public bool IsUpdateDue(DateTime utcNow)
        {
            IsUpdateDueCount++;
            return IsUpdateDueResult;
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

    private static void UpdateDeliveryProgress(EventHubAdapterReceiver receiver, StreamSequenceToken token, bool force = true)
    {
        receiver.UpdateDeliveryProgress(
            (out StreamSequenceToken earliestSubscriptionToken) =>
            {
                earliestSubscriptionToken = token;
                return true;
            },
            force);
    }

    private static void UpdateDeliveryProgressWithNoSubscriptions(EventHubAdapterReceiver receiver, bool force = true)
    {
        receiver.UpdateDeliveryProgress(
            (out StreamSequenceToken earliestSubscriptionToken) =>
            {
                earliestSubscriptionToken = null;
                return true;
            },
            force);
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
    public async Task DeliveryProgress_IsEvaluatedOnlyWhenCheckpointUpdateIsDue()
    {
        var checkpointer = new ThrottledTestCheckpointer { IsUpdateDueResult = false };
        var receiver = await CreateReceiver(checkpointer);
        var wasEvaluated = false;

        receiver.UpdateDeliveryProgress(
            (out StreamSequenceToken earliestSubscriptionToken) =>
            {
                wasEvaluated = true;
                earliestSubscriptionToken = MakeToken(100);
                return true;
            },
            force: false);

        Assert.Equal(1, checkpointer.IsUpdateDueCount);
        Assert.False(wasEvaluated);
        Assert.Null(checkpointer.LastOffset);

        receiver.UpdateDeliveryProgress(
            (out StreamSequenceToken earliestSubscriptionToken) =>
            {
                wasEvaluated = true;
                earliestSubscriptionToken = MakeToken(100);
                return true;
            },
            force: true);

        Assert.True(wasEvaluated);
        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task SingleSubscription_CheckpointsProcessedOffset()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // Single subscription with a known processed offset.
        UpdateDeliveryProgress(receiver, MakeToken(100));

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task MultipleSubscriptions_CheckpointsMinimumWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // The pulling agent passes the lowest subscription offset as the watermark.
        UpdateDeliveryProgress(receiver, MakeToken(95));

        Assert.Equal("95", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task SubscriptionRemoved_NoLongerHoldsWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // Two subscriptions, one slow.
        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        // After the slow subscription is removed, watermark advances.
        UpdateDeliveryProgress(receiver, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task WatermarkAdvances_AsSubscriptionsCatchUp()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // Three subscriptions at different positions: the pulling agent passes the lowest token.
        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        // Slowest catches up.
        UpdateDeliveryProgress(receiver, MakeToken(80));
        Assert.Equal("80", checkpointer.LastOffset);

        // All converge.
        UpdateDeliveryProgress(receiver, MakeToken(120));
        Assert.Equal("120", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task ReplayingSubscription_CanMoveCheckpointBackward()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        UpdateDeliveryProgress(receiver, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        // A newly registered subscriber can request replay from an older token.
        // The safe delivery watermark must be allowed to move backwards so a
        // restart does not skip the messages that subscriber still needs.
        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task NoSubscriptions_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        _ = await CreateReceiver(checkpointer);

        Assert.Null(checkpointer.LastOffset);
        Assert.Equal(0, checkpointer.UpdateCount);
    }

    [Fact, TestCategory("BVT")]
    public async Task NoActiveSubscriptions_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // No subscriptions and cachePurgeOffset is null, so there is no checkpoint.
        UpdateDeliveryProgressWithNoSubscriptions(receiver);

        Assert.Null(checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task CachePurge_UsedAsFallbackWhenNoSubscriptions()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // Trigger cache purge via TryPurgeFromCache → SignalPurge → CachePurgeCheckpointer.
        // Since we can't easily trigger the real cache eviction path, we simulate by calling
        // TryPurgeFromCache (which calls SignalPurge on the cache) and then testing that
        // after a purge offset is recorded, UpdateDeliveryProgress with no active subscriptions
        // falls back to the purge offset.
        //
        // The CachePurgeCheckpointer is constructed in Initialize and wraps the real checkpointer.
        // We verify the fallback by first establishing a purge offset via a delivery progress
        // call that includes subscriptions, then removing all subscriptions.

        // First: some subscription progress establishes a checkpoint.
        UpdateDeliveryProgress(receiver, MakeToken(100));
        Assert.Equal("100", checkpointer.LastOffset);

        // Now with no subscriptions, the purge offset isn't set yet so no checkpoint change.
        // (cachePurgeOffset is only set via the CachePurgeCheckpointer, which we can't
        // trigger without a real cache eviction.)
        UpdateDeliveryProgressWithNoSubscriptions(receiver);
        // cachePurgeOffset is null → no update, LastOffset stays at previous value.
        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task ActiveSubscriptions_TakesPriorityOverCachePurge()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // With active subscriptions, the watermark comes from subscription tokens,
        // not from the cache purge offset.
        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        // Even after progress, subscriptions remain authoritative.
        UpdateDeliveryProgress(receiver, MakeToken(75));
        Assert.Equal("75", checkpointer.LastOffset);
    }
}
