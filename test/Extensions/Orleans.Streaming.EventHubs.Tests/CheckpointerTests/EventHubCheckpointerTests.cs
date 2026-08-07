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
        public bool CheckpointExists { get; init; } = true;
        public string LoadedOffset { get; init; } = EventHubConstants.StartOfStream;
        public string? LastOffset { get; private set; }
        public int UpdateCount { get; private set; }
        public string? FlushedOffset { get; private set; }
        public int FlushCount { get; private set; }

        public Task<string> Load() => Task.FromResult(LoadedOffset);

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
            FlushedOffset = LastOffset;
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
        private readonly IStreamQueueCheckpointer<string>? checkpointer;

        public TestEventHubQueueCache(IStreamQueueCheckpointer<string>? checkpointer = null)
        {
            this.checkpointer = checkpointer;
        }

        public int DisposeCount { get; private set; }
        public int AddCount { get; private set; }
        public string? PurgeOffsetToReport { get; set; }
        public object Cursor { get; } = new();
        public object? RefreshedCursor { get; private set; }
        public StreamSequenceToken? RefreshToken { get; private set; }

        public int GetMaxAddCount() => 1_000;

        public List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc)
        {
            AddCount++;
            return [];
        }

        public object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken) => Cursor;

        public void Refresh(object cursor, StreamSequenceToken? sequenceToken)
        {
            RefreshedCursor = cursor;
            RefreshToken = sequenceToken;
        }

        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            message = null!;
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

    private sealed class NullReturningEventHubReceiver : IEventHubReceiver
    {
        public int ReceiveCount { get; private set; }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            ReceiveCount++;
            // Simulate a receiver binary compiled before the return value was annotated as non-null.
            return Task.FromResult<IEnumerable<EventData>>(null!);
        }

        public Task CloseAsync() => Task.CompletedTask;
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
        receiver.UpdateDeliveryProgress(null!, DateTime.UtcNow);
    }

    private static async Task<EventHubAdapterReceiver> CreateReceiver(
        TestCheckpointer checkpointer,
        TestEventHubQueueCache? cache = null,
        IEventHubReceiver? eventHubReceiver = null,
        Action<string>? onReceiverCreated = null)
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
            eventHubReceiverFactory: (_, offset, _) =>
            {
                onReceiverCreated?.Invoke(offset);
                return eventHubReceiver ?? new TestEventHubReceiver();
            });

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        return receiver;
    }

    [Fact, TestCategory("BVT")]
    public async Task Initialize_WhenCheckpointDoesNotExist_UsesStartOfStream()
    {
        string? receiverOffset = null;
        var checkpointer = new TestCheckpointer
        {
            CheckpointExists = false,
            LoadedOffset = string.Empty,
        };

        _ = await CreateReceiver(
            checkpointer,
            onReceiverCreated: offset => receiverOffset = offset);

        Assert.Equal(EventHubConstants.StartOfStream, receiverOffset);
    }

    [Fact, TestCategory("BVT")]
    public async Task GetQueueMessagesAsync_TreatsNullReceiverResultAsEmpty()
    {
        var cache = new TestEventHubQueueCache();
        var eventHubReceiver = new NullReturningEventHubReceiver();
        var receiver = await CreateReceiver(new TestCheckpointer(), cache, eventHubReceiver);

        var messages = await receiver.GetQueueMessagesAsync(10);

        Assert.Empty(messages);
        Assert.Equal(1, eventHubReceiver.ReceiveCount);
        Assert.Equal(0, cache.AddCount);
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
        var checkpointer = CreateUninitializedCheckpointer();

        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.False(checkpointer.CheckpointExists);
        Assert.Equal(string.Empty, GetLatestOffset(checkpointer));
    }

    [Theory, TestCategory("BVT")]
    [InlineData("20")]
    [InlineData("10")]
    [InlineData("not-an-offset")]
    public async Task Update_WhenOffsetDoesNotAdvance_DoesNotChangeCheckpoint(string candidate)
    {
        var checkpointer = CreateUninitializedCheckpointer();
        SetPersistedOffset(checkpointer, "20");

        checkpointer.Update(candidate, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.True(checkpointer.CheckpointExists);
        Assert.Equal("20", GetLatestOffset(checkpointer));
        Assert.Equal("20", GetEntityOffset(checkpointer));
    }

    [Fact, TestCategory("BVT")]
    public void Update_WhenOffsetAdvances_TracksLatestCheckpoint()
    {
        var checkpointer = CreateUninitializedCheckpointer();
        SetPersistedOffset(checkpointer, "20");

        checkpointer.Update("21", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(checkpointer.CheckpointExists);
        Assert.Equal("21", GetLatestOffset(checkpointer));
        Assert.Equal("21", GetEntityOffset(checkpointer));
    }

    [Fact, TestCategory("BVT")]
    public void Update_WithNoComparer_TracksOpaqueCheckpoint()
    {
        var checkpointer = CreateUninitializedCheckpointer(useNumericComparer: false);

        checkpointer.Update("opaque-checkpoint", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(checkpointer.CheckpointExists);
        Assert.Equal("opaque-checkpoint", GetLatestOffset(checkpointer));
        Assert.Equal("opaque-checkpoint", GetEntityOffset(checkpointer));
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
    public async Task Shutdown_FlushesLatestDeliveryProgress()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        UpdateDeliveryProgress(receiver, MakeToken(100));
        Assert.Equal("100", checkpointer.LastOffset);
        Assert.Equal(0, checkpointer.FlushCount);

        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal(1, checkpointer.FlushCount);
        Assert.Equal("100", checkpointer.FlushedOffset);
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

    private static AzureTableStreamQueueCheckpointer CreateUninitializedCheckpointer(
        IComparer<string>? checkpointComparer = null,
        bool useNumericComparer = true)
    {
        var constructor = typeof(AzureTableStreamQueueCheckpointer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(Orleans.Configuration.AzureTableStreamCheckpointerOptions),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(Microsoft.Extensions.Logging.ILoggerFactory),
                typeof(IComparer<string>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return (AzureTableStreamQueueCheckpointer)constructor.Invoke(
        [
            new Orleans.Configuration.AzureTableStreamCheckpointerOptions
            {
                CheckpointComparer = useNumericComparer ? checkpointComparer ?? StreamCheckpointComparers.Numeric : checkpointComparer,
            },
            "provider",
            "partition",
            "service",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            null,
        ]);
    }

    private static void SetPersistedOffset(AzureTableStreamQueueCheckpointer checkpointer, string offset)
    {
        SetField(checkpointer, "_latestCheckpoint", offset);
        SetField(checkpointer, "_persistedCheckpoint", offset);
        SetEntityOffset(checkpointer, offset);
    }

    private static string GetLatestOffset(AzureTableStreamQueueCheckpointer checkpointer)
        => (string)GetField(checkpointer, "_latestCheckpoint");

    private static string GetEntityOffset(AzureTableStreamQueueCheckpointer checkpointer)
        => (string)GetField(checkpointer, "_entity")
            .GetType()
            .GetProperty("Offset")!
            .GetValue(GetField(checkpointer, "_entity"))!;

    private static void SetEntityOffset(AzureTableStreamQueueCheckpointer checkpointer, string offset)
    {
        var entity = GetField(checkpointer, "_entity");
        entity.GetType().GetProperty("Offset")!.SetValue(entity, offset);
    }

    private static object GetField(AzureTableStreamQueueCheckpointer checkpointer, string name)
    {
        var field = typeof(AzureTableStreamQueueCheckpointer).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(checkpointer)!;
    }

    private static void SetField(AzureTableStreamQueueCheckpointer checkpointer, string name, object value)
    {
        var field = typeof(AzureTableStreamQueueCheckpointer).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(checkpointer, value);
    }
}
