using System.Globalization;
using System.Reflection;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Streams;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

/// <summary>
/// Tests for EventHub delivery-based checkpointing via pulling-agent progress snapshots.
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
        public int FlushCount { get; private set; }

        public Task<string> Load() => Task.FromResult("-1");

        public void Update(string offset, DateTime utcNow)
        {
            if (LastOffset is not null
                && long.Parse(offset, CultureInfo.InvariantCulture) <= long.Parse(LastOffset, CultureInfo.InvariantCulture))
            {
                return;
            }

            LastOffset = offset;
            UpdateCount++;
        }

        public virtual Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

    }

    private sealed class FailingFlushCheckpointer : TestCheckpointer
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            _ = base.FlushAsync(cancellationToken);
            throw new InvalidOperationException("Flush failed");
        }
    }

    private sealed class TestEventHubQueueCache : IEventHubQueueCache
    {
        private readonly IStreamQueueCheckpointer<string> checkpointer;

        public TestEventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer = null)
        {
            this.checkpointer = checkpointer;
        }

        public int DisposeCount { get; private set; }
        public string PurgeOffsetToReport { get; set; }
        public object Cursor { get; } = new();
        public object RefreshedCursor { get; private set; }
        public StreamSequenceToken RefreshToken { get; private set; }

        public int GetMaxAddCount() => 1_000;

        public List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc) => [];

        public object GetCursor(StreamId streamId, StreamSequenceToken sequenceToken) => Cursor;

        public void Refresh(object cursor, StreamSequenceToken sequenceToken)
        {
            RefreshedCursor = cursor;
            RefreshToken = sequenceToken;
        }

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
            if (PurgeOffsetToReport is not null)
            {
                checkpointer?.Update(PurgeOffsetToReport, DateTime.UtcNow);
            }
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class TestEventHubReceiver : IEventHubReceiver
    {
        public int CloseCount { get; private set; }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            return Task.FromResult<IEnumerable<EventData>>([]);
        }

        public Task CloseAsync()
        {
            CloseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingEventHubReceiver : IEventHubReceiver
    {
        public int CloseCount { get; private set; }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            return Task.FromResult<IEnumerable<EventData>>([]);
        }

        public Task CloseAsync() => CloseAsync(CancellationToken.None);

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private static EventHubSequenceToken MakeToken(long offset, long sequenceNumber = 0)
    {
        return new EventHubSequenceToken(offset.ToString(), sequenceNumber, 0);
    }

    private static void UpdateDeliveryProgress(EventHubAdapterReceiver receiver, StreamSequenceToken token)
    {
        receiver.UpdateDeliveryProgress(token, DateTime.UtcNow);
    }

    private static void UpdateDeliveryProgressWithNoSubscriptions(EventHubAdapterReceiver receiver)
    {
        receiver.UpdateDeliveryProgress(null, DateTime.UtcNow);
    }

    private static async Task<EventHubAdapterReceiver> CreateReceiver(
        TestCheckpointer checkpointer,
        TestEventHubQueueCache cache = null,
        IEventHubReceiver eventHubReceiver = null)
    {
        var settings = new EventHubPartitionSettings
        {
            Hub = new Orleans.Configuration.EventHubOptions(),
            Partition = "TestPartition",
            ReceiverOptions = new Orleans.Configuration.EventHubReceiverOptions()
        };
        var instruments = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .BuildServiceProvider()
            .GetRequiredService<OrleansInstruments>();

        var receiver = new EventHubAdapterReceiver(
            settings,
            cacheFactory: (_, createdCheckpointer, _) => cache ?? new TestEventHubQueueCache(createdCheckpointer),
            checkpointerFactory: _ => Task.FromResult<IStreamQueueCheckpointer<string>>(checkpointer),
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            monitor: new Orleans.Streaming.EventHubs.DefaultEventHubReceiverMonitor(
                new EventHubReceiverMonitorDimensions
                {
                    EventHubPartition = settings.Partition,
                    EventHubPath = settings.Hub.EventHubName,
                },
                instruments),
            loadSheddingOptions: new Orleans.Configuration.LoadSheddingOptions(),
            environmentStatisticsProvider: new Orleans.Statistics.EnvironmentStatisticsProvider(),
            eventHubReceiverFactory: (_, _, _) => eventHubReceiver ?? new TestEventHubReceiver());

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        return receiver;
    }

    [Fact, TestCategory("BVT")]
    public async Task CursorRefresh_DelegatesToEventHubCache()
    {
        var cache = new TestEventHubQueueCache();
        var receiver = await CreateReceiver(new TestCheckpointer(), cache);
        var cursor = receiver.GetCacheCursor(StreamId.Create("namespace", Guid.NewGuid()), MakeToken(1));
        var refreshToken = MakeToken(2);

        cursor.Refresh(refreshToken);

        Assert.Same(cache.Cursor, cache.RefreshedCursor);
        Assert.Same(refreshToken, cache.RefreshToken);
    }

    [Fact, TestCategory("BVT")]
    public async Task Shutdown_DisposesCacheAndClosesReceiver_WhenFlushFails()
    {
        var checkpointer = new FailingFlushCheckpointer();
        var cache = new TestEventHubQueueCache();
        var eventHubReceiver = new TestEventHubReceiver();
        var receiver = await CreateReceiver(checkpointer, cache, eventHubReceiver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Shutdown(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, checkpointer.FlushCount);
        Assert.Equal(1, cache.DisposeCount);
        Assert.Equal(1, eventHubReceiver.CloseCount);
    }

    [Fact, TestCategory("BVT")]
    public async Task Shutdown_CancelsBlockedReceiverClose()
    {
        var checkpointer = new TestCheckpointer();
        var cache = new TestEventHubQueueCache();
        var eventHubReceiver = new BlockingEventHubReceiver();
        var receiver = await CreateReceiver(checkpointer, cache, eventHubReceiver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.Shutdown(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, checkpointer.FlushCount);
        Assert.Equal(1, cache.DisposeCount);
        Assert.Equal(1, eventHubReceiver.CloseCount);
    }

    [Fact, TestCategory("BVT")]
    public async Task FlushBeforeLoad_DoesNotPersistUninitializedOffset()
    {
        var constructor = typeof(EventHubCheckpointer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(Orleans.Configuration.AzureTableStreamCheckpointerOptions), typeof(string), typeof(string), typeof(string), typeof(Microsoft.Extensions.Logging.ILoggerFactory) },
            modifiers: null);
        Assert.NotNull(constructor);

        var checkpointer = (EventHubCheckpointer)constructor.Invoke(new object[]
        {
            new Orleans.Configuration.AzureTableStreamCheckpointerOptions(),
            "provider",
            "partition",
            "service",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
        });

        await checkpointer.FlushAsync(CancellationToken.None);
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
    public async Task ReplayingSubscription_DoesNotMoveCheckpointBackward()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        UpdateDeliveryProgress(receiver, MakeToken(200));
        Assert.Equal("200", checkpointer.LastOffset);

        // A newly registered subscriber can request replay from an older token,
        // but the checkpoint only advances and must not move backwards.
        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("200", checkpointer.LastOffset);
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

        // No subscription progress is available; cache purge checkpointing is handled directly by the cache.
        UpdateDeliveryProgressWithNoSubscriptions(receiver);

        Assert.Null(checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task CachePurge_UpdatesCheckpointDirectly()
    {
        var checkpointer = new TestCheckpointer();
        var cache = new TestEventHubQueueCache(checkpointer) { PurgeOffsetToReport = "100" };
        var receiver = await CreateReceiver(checkpointer, cache);

        receiver.TryPurgeFromCache(out _);

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task DeliveryProgress_UpdatesCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        UpdateDeliveryProgress(receiver, MakeToken(50));
        Assert.Equal("50", checkpointer.LastOffset);

        UpdateDeliveryProgress(receiver, MakeToken(75));
        Assert.Equal("75", checkpointer.LastOffset);
    }
}
