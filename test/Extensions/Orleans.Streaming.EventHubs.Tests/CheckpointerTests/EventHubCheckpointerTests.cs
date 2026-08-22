using System.Globalization;
using System.Reflection;
using Azure;
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
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming")]
public class EventHubCheckpointerTests
{
    /// <summary>
    /// A test checkpointer that records all updates for verification.
    /// </summary>
    private class TestCheckpointer : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists { get; set; } = true;
        public string LoadedOffset { get; init; } = EventHubConstants.StartOfStream;
        public string? LastOffset { get; private set; }
        public int UpdateCount { get; private set; }
        public string? FlushedOffset { get; private set; }
        public int FlushCount { get; private set; }
        public int ResetCount { get; private set; }

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

        public Task Reset()
        {
            CheckpointExists = false;
            ResetCount++;
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
                checkpointer?.Update(PurgeOffsetToReport, DateTime.UtcNow, CancellationToken.None);
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

    private sealed class FailingEventHubReceiver : IEventHubReceiver
    {
        public int CloseCount { get; private set; }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime) =>
            throw new ArgumentException("The checkpoint offset is outside the retained Event Hubs range.");

        public Task CloseAsync()
        {
            CloseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableEventHubReceiver : IEventHubReceiver
    {
        public TaskCompletionSource<CancellationToken> ReceiveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
            => Task.FromResult<IEnumerable<EventData>>([]);

        public async Task<IEnumerable<EventData>> ReceiveAsync(
            int maxCount,
            TimeSpan waitTime,
            CancellationToken cancellationToken)
        {
            ReceiveStarted.SetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
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
        Action<string>? onReceiverCreated = null,
        Func<IStreamQueueCheckpointer<string>, IEventHubQueueCache>? createCache = null,
        Func<IEventHubReceiver>? createEventHubReceiver = null)
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
            cacheFactory: (_, createdCheckpointer, _) =>
                createCache?.Invoke(createdCheckpointer) ?? cache ?? new TestEventHubQueueCache(createdCheckpointer),
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
                return createEventHubReceiver?.Invoke() ?? eventHubReceiver ?? new TestEventHubReceiver();
            });

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        return receiver;
    }

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task GetQueueMessagesAsync_TreatsNullReceiverResultAsEmpty()
    {
        var cache = new TestEventHubQueueCache();
        var eventHubReceiver = new NullReturningEventHubReceiver();
        var receiver = await CreateReceiver(new TestCheckpointer(), cache, eventHubReceiver);

        var messages = await receiver.GetQueueMessagesAsync(10, CancellationToken.None);

        Assert.Empty(messages);
        Assert.Equal(1, eventHubReceiver.ReceiveCount);
        Assert.Equal(0, cache.AddCount);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task GetQueueMessagesAsync_ForwardsCancellationToken()
    {
        var eventHubReceiver = new CancellableEventHubReceiver();
        var receiver = await CreateReceiver(
            new TestCheckpointer(),
            eventHubReceiver: eventHubReceiver);
        using var cancellation = new CancellationTokenSource();

        var operation = receiver.GetQueueMessagesAsync(10, cancellation.Token);
        Assert.Equal(cancellation.Token, await eventHubReceiver.ReceiveStarted.Task);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task GetQueueMessagesAsync_StaleCheckpointResetsAndReinitializes()
    {
        var checkpointer = new TestCheckpointer { LoadedOffset = "123" };
        var firstReceiver = new FailingEventHubReceiver();
        var receivers = new Queue<IEventHubReceiver>([firstReceiver, new TestEventHubReceiver()]);
        var caches = new List<TestEventHubQueueCache>();
        var offsets = new List<string>();
        var receiver = await CreateReceiver(
            checkpointer,
            onReceiverCreated: offsets.Add,
            createCache: createdCheckpointer =>
            {
                var created = new TestEventHubQueueCache(createdCheckpointer);
                caches.Add(created);
                return created;
            },
            createEventHubReceiver: receivers.Dequeue);

        await Assert.ThrowsAsync<ArgumentException>(
            () => receiver.GetQueueMessagesAsync(10, CancellationToken.None));

        Assert.Equal(1, checkpointer.ResetCount);
        Assert.Equal(1, caches[0].DisposeCount);
        Assert.Equal(1, firstReceiver.CloseCount);
        Assert.Equal(0, receiver.GetMaxAddCount());

        Assert.Empty(await receiver.GetQueueMessagesAsync(10, CancellationToken.None));
        Assert.Equal(2, caches.Count);
        Assert.Equal([checkpointer.LoadedOffset, EventHubConstants.StartOfStream], offsets);
        Assert.Equal(1_000, receiver.GetMaxAddCount());
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task CancellationOverloads_FallBackToLegacyReceiver()
    {
        IEventHubReceiver receiver = new TestEventHubReceiver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.Zero, cancellation.Token));
        await receiver.CloseAsync(cancellation.Token);

        Assert.Equal(1, ((TestEventHubReceiver)receiver).CloseCount);
    }

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task FlushBeforeLoad_DoesNotPersistUninitializedOffset()
    {
        var checkpointer = CreateUninitializedCheckpointer();

        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.False(checkpointer.CheckpointExists);
        Assert.Equal(string.Empty, GetLatestOffset(checkpointer));
    }

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public void Update_WithNoComparer_TracksOpaqueCheckpoint()
    {
        var checkpointer = CreateUninitializedCheckpointer(useNumericComparer: false);

        checkpointer.Update("opaque-checkpoint", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(checkpointer.CheckpointExists);
        Assert.Equal("opaque-checkpoint", GetLatestOffset(checkpointer));
        Assert.Equal("opaque-checkpoint", GetEntityOffset(checkpointer));
    }

    [TestSuite("BVT")]
    [Theory, TestCategory("BVT")]
    [InlineData("", "provider_service")]
    [InlineData("EventHubCheckpoints_", "EventHubCheckpoints_provider_service")]
    public void PartitionKeyPrefix_IsAppliedPerCheckpointer(string partitionKeyPrefix, string expected)
    {
        var checkpointer = CreateUninitializedCheckpointer(partitionKeyPrefix: partitionKeyPrefix);

        Assert.Equal(expected, GetEntityPartitionKey(checkpointer));
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public void EventHubCheckpointEntity_PreservesLegacyAzureTableSchema()
    {
        var checkpointer = CreateUninitializedCheckpointer(
            partitionKeyPrefix: "EventHubCheckpoints_",
            streamProviderName: "provider/name",
            partition: "partition?1",
            serviceId: "service#id");
        var entity = GetField(checkpointer, "_entity");
        var entityType = entity.GetType();

        Assert.Equal("EventHubCheckpoints_provider_name_service_id", GetEntityPartitionKey(checkpointer));
        Assert.Equal("partition_partition_1", GetEntityRowKey(checkpointer));
        Assert.Equal(typeof(string), entityType.GetProperty("Offset")?.PropertyType);
        Assert.Equal(typeof(string), entityType.GetProperty("PartitionKey")?.PropertyType);
        Assert.Equal(typeof(string), entityType.GetProperty("RowKey")?.PropertyType);
        Assert.Equal(typeof(DateTimeOffset?), entityType.GetProperty("Timestamp")?.PropertyType);
        Assert.Equal(typeof(ETag), entityType.GetProperty("ETag")?.PropertyType);
    }

    [TestSuite("BVT")]
    [Theory, TestCategory("BVT")]
    [InlineData(false, "", EventHubConstants.StartOfStream)]
    [InlineData(true, "123", "123")]
    public async Task EventHubCompatibilityWrapper_PreservesLoadSemantics(
        bool checkpointExists,
        string loadedOffset,
        string expected)
    {
        var inner = new TestCheckpointer
        {
            CheckpointExists = checkpointExists,
            LoadedOffset = loadedOffset,
        };
        var constructor = typeof(EventHubCheckpointer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IStreamQueueCheckpointer<string>)],
            modifiers: null);
        Assert.NotNull(constructor);
        var checkpointer = (EventHubCheckpointer)constructor.Invoke([inner]);

        Assert.Equal(expected, await checkpointer.Load(CancellationToken.None));
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task EventHubCompatibilityWrapper_ForwardsReset()
    {
        var inner = new TestCheckpointer();
        var constructor = typeof(EventHubCheckpointer).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IStreamQueueCheckpointer<string>)],
            modifiers: null);
        Assert.NotNull(constructor);
        var checkpointer = (EventHubCheckpointer)constructor.Invoke([inner]);

        await checkpointer.Reset(CancellationToken.None);

        Assert.Equal(1, inner.ResetCount);
        Assert.False(checkpointer.CheckpointExists);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task AzureTableReset_DeletesCheckpointAndClearsState()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var options = new Orleans.Configuration.AzureTableStreamCheckpointerOptions
        {
            PersistInterval = TimeSpan.FromMilliseconds(1),
        }.ConfigureTestDefaults();
        var checkpointer = await AzureTableStreamQueueCheckpointer.Create(
            options,
            $"provider-{suffix}",
            $"partition-{suffix}",
            $"service-{suffix}",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        checkpointer.Update("42", DateTime.UtcNow, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);
        Assert.True(checkpointer.CheckpointExists);

        await checkpointer.Reset(CancellationToken.None);

        Assert.False(checkpointer.CheckpointExists);
        var reloaded = await AzureTableStreamQueueCheckpointer.Create(
            options,
            $"provider-{suffix}",
            $"partition-{suffix}",
            $"service-{suffix}",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.Equal(string.Empty, await reloaded.Load(CancellationToken.None));
        Assert.False(reloaded.CheckpointExists);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task SingleSubscription_CheckpointsProcessedOffset()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // Single subscription with a known processed offset.
        UpdateDeliveryProgress(receiver, MakeToken(100));

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task MultipleSubscriptions_CheckpointsMinimumWatermark()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // The pulling agent passes the lowest subscription offset as the watermark.
        UpdateDeliveryProgress(receiver, MakeToken(95));

        Assert.Equal("95", checkpointer.LastOffset);
    }

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
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

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task NoSubscriptions_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        _ = await CreateReceiver(checkpointer);

        Assert.Null(checkpointer.LastOffset);
        Assert.Equal(0, checkpointer.UpdateCount);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task NoActiveSubscriptions_NoCheckpoint()
    {
        var checkpointer = new TestCheckpointer();
        var receiver = await CreateReceiver(checkpointer);

        // No subscription progress is available; cache purge checkpointing is handled directly by the cache.
        UpdateDeliveryProgressWithNoSubscriptions(receiver);

        Assert.Null(checkpointer.LastOffset);
    }

    [TestSuite("BVT")]
    [Fact, TestCategory("BVT")]
    public async Task CachePurge_UpdatesCheckpointDirectly()
    {
        var checkpointer = new TestCheckpointer();
        var cache = new TestEventHubQueueCache(checkpointer) { PurgeOffsetToReport = "100" };
        var receiver = await CreateReceiver(checkpointer, cache);

        receiver.TryPurgeFromCache(out _);

        Assert.Equal("100", checkpointer.LastOffset);
    }

    [TestSuite("BVT")]
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
        bool useNumericComparer = true,
        string? partitionKeyPrefix = null,
        string streamProviderName = "provider",
        string partition = "partition",
        string serviceId = "service")
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
                typeof(string),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return (AzureTableStreamQueueCheckpointer)constructor.Invoke(
        [
            new Orleans.Configuration.AzureTableStreamCheckpointerOptions
            {
                CheckpointComparer = useNumericComparer ? checkpointComparer ?? StreamCheckpointComparers.Numeric : checkpointComparer,
            },
            streamProviderName,
            partition,
            serviceId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            null,
            partitionKeyPrefix,
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

    private static string GetEntityPartitionKey(AzureTableStreamQueueCheckpointer checkpointer)
        => (string)GetField(checkpointer, "_entity")
            .GetType()
            .GetProperty("PartitionKey")!
            .GetValue(GetField(checkpointer, "_entity"))!;

    private static string GetEntityRowKey(AzureTableStreamQueueCheckpointer checkpointer)
        => (string)GetField(checkpointer, "_entity")
            .GetType()
            .GetProperty("RowKey")!
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
