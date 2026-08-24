using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT")]
public sealed class RecoverableStreamReceiverTests
{
    [Fact]
    public async Task Receiver_ResumesAfterCheckpointAndPersistsDeliveryProgress()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var source = new TestSource(
        [
            new TestQueueMessage(streamId, 11, "payload"),
        ]);
        var adapter = new TestDataAdapter();
        var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024));
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            bufferPool,
            adapter,
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var checkpointer = new TestCheckpointer("10");
        var receiver = new RecoverableStreamReceiver<TestQueueMessage>(
            source,
            adapter,
            cache,
            checkpointer,
            startFromNow: true);

        await receiver.Initialize(TimeSpan.FromSeconds(5));

        Assert.Equal("10", source.StartPosition.Checkpoint);
        Assert.True(source.StartPosition.StartFromNow);
        var notifications = await receiver.GetQueueMessagesAsync(100, CancellationToken.None);
        var notification = Assert.Single(notifications);
        Assert.Equal(streamId, notification.StreamId);
        Assert.Equal(11, notification.SequenceToken.SequenceNumber);

        using var cursor = receiver.GetCacheCursor(streamId, notification.SequenceToken);
        Assert.True(cursor.MoveNext());
        var batch = Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out var exception));
        Assert.Null(exception);
        Assert.Equal("payload", batch.Payload);
        Assert.True(adapter.CompareCallCount > 0);
        Assert.False(cursor.MoveNext());
        Assert.Same(batch, cursor.GetCurrent(out exception));
        Assert.Null(exception);

        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(11), DateTime.UtcNow);
        Assert.Equal("11", checkpointer.LastUpdatedCheckpoint);

        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checkpointer.FlushCount);
        Assert.True(source.IsShutdown);
    }

    private sealed class BlockingInitializationCheckpointer : IStreamQueueCheckpointer<string>
    {
        public TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstLoadCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstLoadToComplete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken FirstLoadCancellation { get; private set; }

        public int LoadCount { get; private set; }

        public bool CheckpointExists => false;

        public Task<string> Load() => Load(CancellationToken.None);

        public async Task<string> Load(CancellationToken cancellationToken)
        {
            LoadCount++;
            if (LoadCount == 1)
            {
                FirstLoadCancellation = cancellationToken;
                FirstLoadStarted.TrySetResult();
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    FirstLoadCancellationObserved);
                await FirstLoadCancellationObserved.Task;
                await AllowFirstLoadToComplete.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return string.Empty;
        }

        public void Update(string offset, DateTime utcNow) { }

        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => cancellationToken.ThrowIfCancellationRequested();

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class IndependentlyCanceledCheckpointer : IStreamQueueCheckpointer<string>
    {
        public int LoadCount { get; private set; }

        public bool CheckpointExists => false;

        public Task<string> Load() => Load(CancellationToken.None);

        public async Task<string> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            await Task.Yield();
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }

        public void Update(string offset, DateTime utcNow) { }

        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => cancellationToken.ThrowIfCancellationRequested();

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Receiver_RestartRedeliversInclusiveBatchWhenCheckpointDidNotAdvance()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var messages = new[]
        {
            new TestQueueMessage(streamId, 9, "old-9"),
            new TestQueueMessage(streamId, 10, "old-10"),
            new TestQueueMessage(streamId, 11, "payload"),
        };
        var store = new TestCheckpointStore("10");
        var firstSource = new ReplaySource(messages);
        var firstReceiver = CreateReceiver(
            firstSource,
            new StreamQueueCheckpointer(
                store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = TimeSpan.FromSeconds(1),
                }));
        await firstReceiver.Initialize(TimeSpan.FromSeconds(5));
        var firstNotification = Assert.Single(await firstReceiver.GetQueueMessagesAsync(10, CancellationToken.None));
        using (var cursor = firstReceiver.GetCacheCursor(streamId, firstNotification.SequenceToken))
        {
            Assert.True(cursor.MoveNext());
            Assert.Equal(11, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
            // The inclusive first batch was observed but never confirmed as delivered.
        }

        await firstReceiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal("10", store.State.Checkpoint);

        var secondSource = new ReplaySource(messages);
        var secondReceiver = CreateReceiver(
            secondSource,
            new StreamQueueCheckpointer(
                store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = TimeSpan.FromSeconds(1),
                }));
        await secondReceiver.Initialize(TimeSpan.FromSeconds(5));

        var redelivered = Assert.Single(await secondReceiver.GetQueueMessagesAsync(10, CancellationToken.None));
        Assert.Equal(11, redelivered.SequenceToken.SequenceNumber);
        await secondReceiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receiver_QuietScanCheckpointRestartsAfterBusyTail()
    {
        var quietStream = StreamId.Create("namespace", Guid.NewGuid());
        var busyStream = StreamId.Create("namespace", Guid.NewGuid());
        var initialMessages = new[]
        {
            new TestQueueMessage(quietStream, 1, "quiet"),
            new TestQueueMessage(busyStream, 2, "busy-2"),
            new TestQueueMessage(busyStream, 3, "busy-3"),
        };
        var store = new TestCheckpointStore(string.Empty);
        var receiver = CreateReceiver(
            new ReplaySource(initialMessages),
            new StreamQueueCheckpointer(
                store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = TimeSpan.FromSeconds(1),
                }));
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        var notifications = await receiver.GetQueueMessagesAsync(10, CancellationToken.None);
        using var cursor = receiver.GetCacheCursor(quietStream, notifications[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
        Assert.True(cursor.MoveNext());
        progress.RecordDeliverySuccess();
        Assert.False(cursor.MoveNext());
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);

        receiver.UpdateDeliveryProgress(progress.SafeSequenceToken, DateTime.UtcNow);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal("3", store.State.Checkpoint);

        var restarted = CreateReceiver(
            new ReplaySource(
            [
                .. initialMessages,
                new TestQueueMessage(busyStream, 4, "busy-4"),
            ]),
            new StreamQueueCheckpointer(
                store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = TimeSpan.FromSeconds(1),
                }));
        await restarted.Initialize(TimeSpan.FromSeconds(5));

        var next = Assert.Single(await restarted.GetQueueMessagesAsync(10, CancellationToken.None));
        Assert.Equal(4, next.SequenceToken.SequenceNumber);
        await restarted.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Registry_ReturnsSameReceiverAndCacheInstanceForQueue()
    {
        var created = 0;
        var registry = new QueueAdapterReceiverRegistry<TestCombinedReceiver>(_ =>
        {
            created++;
            return new TestCombinedReceiver();
        });
        var queue = QueueId.GetQueueId("queue", 0, 0);

        IQueueAdapterReceiver receiver = registry.GetOrCreate(queue);
        IQueueCache cache = registry.GetOrCreate(queue);

        Assert.Same(receiver, cache);
        Assert.Equal(1, created);
        Assert.Single(registry.Receivers);
    }

    [Fact]
    public async Task DeliveryProgress_WithNoSubscribers_AdvancesToNewestCachedRecord()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var source = new TestSource([new TestQueueMessage(streamId, 11, "payload")]);
        var adapter = new TestDataAdapter();
        var bufferPool = new TrackingBufferPool();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            bufferPool,
            adapter,
            new ChronologicalEvictionStrategy(
                NullLogger.Instance,
                new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
                cacheMonitor: null,
                monitorWriteInterval: null),
            NullLogger.Instance);
        var checkpointer = new TestCheckpointer("10");
        var receiver = new RecoverableStreamReceiver<TestQueueMessage>(
            source,
            adapter,
            cache,
            checkpointer,
            startFromNow: false);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        _ = await receiver.GetQueueMessagesAsync(100, CancellationToken.None);

        receiver.UpdateDeliveryProgress(earliestSubscriptionToken: null, DateTime.UtcNow);

        Assert.Equal("11", checkpointer.LastUpdatedCheckpoint);
        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(1, bufferPool.FreeCount);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cache_CursorCreatedWhileEmptyReadsFirstLaterRecord()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new TestDataAdapter();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            adapter,
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var initial = cache.Add([new TestQueueMessage(streamId, 10, "initial")], DateTime.UnixEpoch);
        cache.UpdateDeliveryProgress(initial[0].SequenceToken, DateTime.UtcNow);
        Assert.Equal(0, cache.ItemCount);

        using var cursor = cache.GetCacheCursor(streamId, token: null);
        Assert.False(cursor.MoveNext());

        _ = cache.Add([new TestQueueMessage(streamId, 11, "payload")], DateTime.UnixEpoch);

        Assert.True(cursor.MoveNext());
        Assert.Equal("payload", Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).Payload);
    }

    [Fact]
    public void CachePressure_BlocksUnsafeTimePurgeUntilDeliveryProgressAdvances()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new TestDataAdapter();
        var evictionStrategy = new NoOpEvictionStrategy();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            adapter,
            evictionStrategy,
            NullLogger.Instance,
            flowController: new FixedFlowController(0));
        var positions = cache.Add(
            [new TestQueueMessage(streamId, 11, "payload")],
            DateTime.UnixEpoch);

        Assert.True(cache.IsUnderPressure());
        Assert.False(cache.TryPurgeFromCache(out _));
        Assert.Equal(0, evictionStrategy.PerformPurgeCount);
        Assert.Equal(1, cache.ItemCount);

        cache.UpdateDeliveryProgress(positions[0].SequenceToken, DateTime.UtcNow);

        Assert.Equal(0, cache.ItemCount);
    }

    [Fact]
    public void Cache_DisposeDrainsMessagesReturnsBuffersAndAllocatesFreshBufferIfReused()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var bufferPool = new TrackingBufferPool();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            bufferPool,
            new TestDataAdapter(),
            new ChronologicalEvictionStrategy(
                NullLogger.Instance,
                new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
                cacheMonitor: null,
                monitorWriteInterval: null),
            NullLogger.Instance);
        _ = cache.Add([new TestQueueMessage(streamId, 11, "payload")], DateTime.UnixEpoch);

        cache.Dispose();

        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(1, bufferPool.AllocateCount);
        Assert.Equal(1, bufferPool.FreeCount);

        _ = cache.Add([new TestQueueMessage(streamId, 12, "next")], DateTime.UnixEpoch);

        Assert.Equal(1, cache.ItemCount);
        Assert.Equal(2, bufferPool.AllocateCount);
        cache.Dispose();
        Assert.Equal(2, bufferPool.FreeCount);
    }

    [Fact]
    public void Cache_FailedPackingReturnsUncommittedPooledBuffers()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var bufferPool = new TrackingBufferPool();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            bufferPool,
            new ThrowingDataAdapter(),
            new ChronologicalEvictionStrategy(
                NullLogger.Instance,
                new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
                cacheMonitor: null,
                monitorWriteInterval: null),
            NullLogger.Instance);
        var message = new TestQueueMessage(streamId, 1, "payload");

        Assert.Throws<InvalidOperationException>(() => cache.Add([message], DateTime.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => cache.Add([message], DateTime.UnixEpoch));

        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(2, bufferPool.AllocateCount);
        Assert.Equal(2, bufferPool.FreeCount);
    }

    [Fact]
    public void Cache_AddsRawRecordsInOrderAndDecodesLazily()
    {
        var streamA = StreamId.Create("namespace", Guid.NewGuid());
        var streamB = StreamId.Create("namespace", Guid.NewGuid());
        var messages = new[]
        {
            new TestQueueMessage(streamA, 10, "first"),
            new TestQueueMessage(streamB, 11, "second"),
            new TestQueueMessage(streamA, 12, "third"),
        };
        var adapter = new TestDataAdapter();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            adapter,
            new NoOpEvictionStrategy(),
            NullLogger.Instance);

        var positions = cache.Add(messages, DateTime.UnixEpoch);

        Assert.Equal([10, 11, 12], positions.Select(position => position.SequenceToken.SequenceNumber));
        Assert.Equal(3, adapter.PositionCallCount);
        Assert.Equal(3, adapter.FromQueueMessageCallCount);
        Assert.Equal(0, adapter.GetBatchContainerCallCount);

        using var cursor = cache.GetCacheCursor(streamA, positions[0].SequenceToken);
        Assert.True(cursor.MoveNext());
        Assert.Equal("first", Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).Payload);
        Assert.Equal(1, adapter.GetBatchContainerCallCount);
        Assert.True(cursor.MoveNext());
        Assert.Equal("third", Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).Payload);
        Assert.Equal(2, adapter.GetBatchContainerCallCount);
        Assert.False(cursor.MoveNext());
        Assert.Equal(2, adapter.GetBatchContainerCallCount);
    }

    [Fact]
    public void Cache_CursorProgressRequiresDeliveryAndIncludesUnrelatedScans()
    {
        var streamA = StreamId.Create("namespace", Guid.NewGuid());
        var streamB = StreamId.Create("namespace", Guid.NewGuid());
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            new TestDataAdapter(),
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var positions = cache.Add(
        [
            new TestQueueMessage(streamA, 1, "a-1"),
            new TestQueueMessage(streamB, 2, "b-2"),
            new TestQueueMessage(streamB, 3, "b-3"),
            new TestQueueMessage(streamA, 4, "a-4"),
        ],
            DateTime.UnixEpoch);
        using var cursor = cache.GetCacheCursor(streamA, positions[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);

        Assert.True(cursor.MoveNext());
        Assert.Null(progress.SafeSequenceToken);

        progress.RecordDeliverySuccess();
        Assert.Equal(1, progress.SafeSequenceToken?.SequenceNumber);

        Assert.True(cursor.MoveNext());
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);

        progress.RecordDeliverySuccess();
        Assert.Equal(4, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public void Cache_BatchedMatchesRemainPendingUntilWholeDeliverySucceeds()
    {
        var streamA = StreamId.Create("namespace", Guid.NewGuid());
        var streamB = StreamId.Create("namespace", Guid.NewGuid());
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            new TestDataAdapter(),
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var positions = cache.Add(
        [
            new TestQueueMessage(streamA, 1, "a-1"),
            new TestQueueMessage(streamB, 2, "b-2"),
            new TestQueueMessage(streamA, 3, "a-3"),
        ],
            DateTime.UnixEpoch);
        using var cursor = cache.GetCacheCursor(streamA, positions[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);

        Assert.True(cursor.MoveNext());
        Assert.True(cursor.MoveNext());
        Assert.Null(progress.SafeSequenceToken);

        progress.RecordDeliverySuccess();
        Assert.Equal(3, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public void Cache_DeliveredThroughScansIntermediatePartitionRecordsWithoutRedelivery()
    {
        var streamA = StreamId.Create("namespace", Guid.NewGuid());
        var streamB = StreamId.Create("namespace", Guid.NewGuid());
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            new TestDataAdapter(),
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var positions = cache.Add(
        [
            new TestQueueMessage(streamB, 2, "b-2"),
            new TestQueueMessage(streamB, 3, "b-3"),
            new TestQueueMessage(streamA, 10, "a-10"),
            new TestQueueMessage(streamA, 11, "a-11"),
        ],
            DateTime.UnixEpoch);
        using var cursor = cache.GetCacheCursor(streamA, positions[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
        progress.SetDeliveredThrough(new EventSequenceTokenV2(10));

        Assert.True(cursor.MoveNext());
        Assert.Equal(11, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.Equal(10, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public void Cache_DeliveryFailureRewindsToFirstPendingRecord()
    {
        var streamA = StreamId.Create("namespace", Guid.NewGuid());
        var streamB = StreamId.Create("namespace", Guid.NewGuid());
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            new TestDataAdapter(),
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var positions = cache.Add(
        [
            new TestQueueMessage(streamA, 1, "a-1"),
            new TestQueueMessage(streamB, 2, "b-2"),
            new TestQueueMessage(streamA, 3, "a-3"),
        ],
            DateTime.UnixEpoch);
        using var cursor = cache.GetCacheCursor(streamA, positions[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
        Assert.True(cursor.MoveNext());
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        Assert.True(cursor.MoveNext());
        Assert.Equal(3, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);

        cursor.RecordDeliveryFailure();

        Assert.Null(progress.SafeSequenceToken);
        Assert.True(cursor.MoveNext());
        Assert.Equal(1, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        progress.RecordDeliverySuccess();
        Assert.True(cursor.MoveNext());
        Assert.Equal(3, cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
    }

    [Fact]
    public async Task Receiver_MidInitializationCancellationReachesLoadAndAllowsRetry()
    {
        var source = new TestSource([]);
        var checkpointer = new BlockingInitializationCheckpointer();
        var receiver = CreateReceiver(source, checkpointer);
        using var cancellation = new CancellationTokenSource();

        var initialization = receiver.Initialize(cancellation.Token);
        await checkpointer.FirstLoadStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.True(checkpointer.FirstLoadCancellation.IsCancellationRequested);

        var retry = receiver.GetQueueMessagesAsync(10, CancellationToken.None);
        Assert.Equal(1, checkpointer.LoadCount);
        checkpointer.AllowFirstLoadToComplete.TrySetResult();

        Assert.Empty(await retry);
        Assert.Equal(2, checkpointer.LoadCount);
        Assert.Equal(1, source.InitializeCount);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receiver_IndependentInitializationCancellationIsNotRetried()
    {
        var source = new TestSource([]);
        var checkpointer = new IndependentlyCanceledCheckpointer();
        var receiver = CreateReceiver(source, checkpointer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.GetQueueMessagesAsync(10, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, checkpointer.LoadCount);
        Assert.Equal(0, source.InitializeCount);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cache_QuietCursorAdvancesAcrossUnrelatedRecords()
    {
        var quietStream = StreamId.Create("namespace", Guid.NewGuid());
        var busyStream = StreamId.Create("namespace", Guid.NewGuid());
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            new TestDataAdapter(),
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        var positions = cache.Add(
        [
            new TestQueueMessage(quietStream, 1, "quiet"),
            new TestQueueMessage(busyStream, 2, "busy-2"),
            new TestQueueMessage(busyStream, 3, "busy-3"),
            new TestQueueMessage(busyStream, 4, "busy-4"),
        ],
            DateTime.UnixEpoch);
        using var cursor = cache.GetCacheCursor(quietStream, positions[0].SequenceToken);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
        Assert.True(cursor.MoveNext());
        progress.RecordDeliverySuccess();

        Assert.False(cursor.MoveNext());

        Assert.Equal(4, progress.SafeSequenceToken?.SequenceNumber);
    }

    [Fact]
    public async Task Receiver_RetriesInitializationOnNextRead()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var source = new TestSource(
            [new TestQueueMessage(streamId, 1, "payload")],
            initializationFailures: 1);
        var receiver = CreateReceiver(source, new TestCheckpointer(string.Empty));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receiver.GetQueueMessagesAsync(10, CancellationToken.None));
        var messages = await receiver.GetQueueMessagesAsync(10, CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal(2, source.InitializeCount);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receiver_PreCanceledReadDoesNotInitialize()
    {
        var source = new TestSource([]);
        var receiver = CreateReceiver(source, new TestCheckpointer(string.Empty));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.GetQueueMessagesAsync(10, cancellation.Token));

        Assert.Equal(0, source.InitializeCount);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_WhenFlushFails_StillShutsDownSource()
    {
        var source = new TestSource([]);
        var expected = new InvalidOperationException("flush failed");
        var receiver = CreateReceiver(
            source,
            new TestCheckpointer(string.Empty) { FlushException = expected });
        await receiver.Initialize(TimeSpan.FromSeconds(5));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => receiver.Shutdown(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, actual);
        Assert.True(source.IsShutdown);
    }

    [Fact]
    public async Task Registry_ConcurrentRequestsReturnSameWinningInstance()
    {
        const int participantCount = 3;
        var queue = QueueId.GetQueueId("queue", 0, 0);
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var registry = new QueueAdapterReceiverRegistry<TestCombinedReceiver>(_ =>
        {
            Interlocked.Increment(ref created);
            factoryStarted.TrySetResult();
            releaseFactory.Task.GetAwaiter().GetResult();
            return new TestCombinedReceiver();
        });

        var requests = Enumerable.Range(0, participantCount)
            .Select(_ => Task.Run(() => registry.GetOrCreate(queue)))
            .ToArray();
        await factoryStarted.Task;
        releaseFactory.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, created);
        Assert.Same(results[0], Assert.Single(registry.Receivers).Value);
    }

    [Fact]
    public void Registry_RemoveAllowsFreshReceiverForReassignedQueue()
    {
        var queue = QueueId.GetQueueId("queue", 0, 0);
        var registry = new QueueAdapterReceiverRegistry<TestCombinedReceiver>(
            _ => new TestCombinedReceiver());
        var first = registry.GetOrCreate(queue);

        Assert.True(registry.Remove(queue, first));
        var second = registry.GetOrCreate(queue);

        Assert.NotSame(first, second);
        Assert.Same(second, Assert.Single(registry.Receivers).Value);
    }

    private static RecoverableStreamReceiver<TestQueueMessage> CreateReceiver(
        IRecoverableStreamSource<TestQueueMessage> source,
        IStreamQueueCheckpointer<string> checkpointer)
    {
        var adapter = new TestDataAdapter();
        var cache = new RecoverableStreamQueueCache<TestQueueMessage>(
            100,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            adapter,
            new NoOpEvictionStrategy(),
            NullLogger.Instance);
        return new(source, adapter, cache, checkpointer, startFromNow: false);
    }

    private sealed record TestQueueMessage(StreamId StreamId, long SequenceNumber, string Payload);

    private sealed class TestDataAdapter : IRecoverableStreamDataAdapter<TestQueueMessage>
    {
        public int CompareCallCount { get; private set; }
        public int PositionCallCount { get; private set; }
        public int FromQueueMessageCallCount { get; private set; }
        public int GetBatchContainerCallCount { get; private set; }

        public StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
        {
            PositionCallCount++;
            return new(queueMessage.StreamId, new EventSequenceTokenV2(queueMessage.SequenceNumber));
        }

        public CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            TestQueueMessage queueMessage,
            DateTime dequeueTimeUtc,
            Func<int, ArraySegment<byte>> getSegment)
        {
            FromQueueMessageCallCount++;
            var size = SegmentBuilder.CalculateAppendSize(queueMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture))
                + SegmentBuilder.CalculateAppendSize(queueMessage.Payload);
            var segment = getSegment(size);
            var offset = 0;
            SegmentBuilder.Append(segment, ref offset, queueMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture));
            SegmentBuilder.Append(segment, ref offset, queueMessage.Payload);
            return new CachedMessage
            {
                StreamId = streamPosition.StreamId,
                SequenceNumber = queueMessage.SequenceNumber,
                EventIndex = streamPosition.SequenceToken.EventIndex,
                EnqueueTimeUtc = dequeueTimeUtc,
                DequeueTimeUtc = dequeueTimeUtc,
                Segment = segment,
            };
        }

        public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            GetBatchContainerCallCount++;
            var offset = 0;
            _ = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset);
            var payload = SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset);
            return new TestBatchContainer(
                cachedMessage.StreamId,
                GetSequenceToken(ref cachedMessage),
                payload!);
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            => new EventSequenceTokenV2(cachedMessage.SequenceNumber, cachedMessage.EventIndex);

        public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
        {
            CompareCallCount++;
            return cachedMessage.SequenceNumber != token.SequenceNumber
                ? cachedMessage.SequenceNumber.CompareTo(token.SequenceNumber)
                : cachedMessage.EventIndex.CompareTo(token.EventIndex);
        }

        public string GetOffset(ref CachedMessage cachedMessage)
        {
            var offset = 0;
            return SegmentBuilder.ReadNextString(cachedMessage.Segment, ref offset)!;
        }

        public bool TryGetOffset(StreamSequenceToken token, out string offset)
        {
            offset = token.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    private sealed class ThrowingDataAdapter : IRecoverableStreamDataAdapter<TestQueueMessage>
    {
        public StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            => new(queueMessage.StreamId, new EventSequenceTokenV2(queueMessage.SequenceNumber));

        public CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            TestQueueMessage queueMessage,
            DateTime dequeueTimeUtc,
            Func<int, ArraySegment<byte>> getSegment)
        {
            _ = getSegment(16);
            throw new InvalidOperationException("packing failed");
        }

        public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
            => throw new NotSupportedException();

        public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            => new EventSequenceTokenV2(cachedMessage.SequenceNumber);

        public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
            => cachedMessage.SequenceNumber.CompareTo(token.SequenceNumber);

        public string GetOffset(ref CachedMessage cachedMessage)
            => cachedMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture);

        public bool TryGetOffset(StreamSequenceToken token, out string offset)
        {
            offset = token.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    private sealed class TestBatchContainer(
        StreamId streamId,
        StreamSequenceToken sequenceToken,
        string payload) : IBatchContainer
    {
        public StreamId StreamId { get; } = streamId;

        public StreamSequenceToken SequenceToken { get; } = sequenceToken;

        public string Payload { get; } = payload;

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];

        public bool ImportRequestContext() => false;
    }

    private sealed class TestSource(
        IReadOnlyList<TestQueueMessage> messages,
        int initializationFailures = 0) : IRecoverableStreamSource<TestQueueMessage>
    {
        private bool read;
        private int remainingInitializationFailures = initializationFailures;

        public RecoverableStreamStartPosition StartPosition { get; private set; }

        public bool IsShutdown { get; private set; }
        public int InitializeCount { get; private set; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            if (remainingInitializationFailures-- > 0)
            {
                throw new InvalidOperationException("initialization failed");
            }

            StartPosition = position;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TestQueueMessage>> Read(int maxCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
            {
                return Task.FromResult<IReadOnlyList<TestQueueMessage>>([]);
            }

            read = true;
            return Task.FromResult(messages);
        }

        public Task Shutdown(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsShutdown = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestCheckpointer(string checkpoint) : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => !string.IsNullOrEmpty(checkpoint);

        public string? LastUpdatedCheckpoint { get; private set; }

        public int FlushCount { get; private set; }
        public Exception? FlushException { get; init; }

        public Task<string> Load() => Task.FromResult(checkpoint);

        public Task<string> Load(CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<string>(cancellationToken)
                : Task.FromResult(checkpoint);

        public void Update(string offset, DateTime utcNow) => LastUpdatedCheckpoint = offset;

        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdatedCheckpoint = offset;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            if (FlushException is not null)
            {
                return Task.FromException(FlushException);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestCheckpointStore(string checkpoint) : IStreamCheckpointStore
    {
        public StreamCheckpointStoreState State { get; private set; } = new(checkpoint, "1");

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(State);
        }

        public ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(State.Version, expectedVersion);
            State = new(checkpoint, (int.Parse(State.Version) + 1).ToString(CultureInfo.InvariantCulture));
            return ValueTask.FromResult(State);
        }
    }

    private sealed class ReplaySource(IReadOnlyList<TestQueueMessage> messages)
        : IRecoverableStreamSource<TestQueueMessage>
    {
        private long checkpoint;
        private bool read;

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint = string.IsNullOrEmpty(position.Checkpoint)
                ? 0
                : long.Parse(position.Checkpoint, CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TestQueueMessage>> Read(int maxCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
            {
                return Task.FromResult<IReadOnlyList<TestQueueMessage>>([]);
            }

            read = true;
            return Task.FromResult<IReadOnlyList<TestQueueMessage>>(
                messages.Where(message => message.SequenceNumber > checkpoint).Take(maxCount).ToList());
        }

        public Task Shutdown(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpEvictionStrategy : IEvictionStrategy
    {
        public int PerformPurgeCount { get; private set; }

        public IPurgeObservable PurgeObservable { private get; set; } = null!;

        public Action<CachedMessage?, CachedMessage?>? OnPurged { get; set; }

        public void PerformPurge(DateTime utcNow)
        {
            PerformPurgeCount++;
        }

        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
        }
    }

    private sealed class FixedFlowController(int maxAddCount) : IQueueFlowController
    {
        public int GetMaxAddCount() => maxAddCount;
    }

    private sealed class TrackingBufferPool : IObjectPool<FixedSizeBuffer>
    {
        public int AllocateCount { get; private set; }

        public int FreeCount { get; private set; }

        public FixedSizeBuffer Allocate()
        {
            AllocateCount++;
            return new(4 * 1024) { Pool = this };
        }

        public void Free(FixedSizeBuffer resource)
        {
            FreeCount++;
        }
    }

    private sealed class TestCombinedReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            => Task.FromResult<IList<IBatchContainer>>([]);

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;

        public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;

        public int GetMaxAddCount() => 1;

        public void AddToCache(IList<IBatchContainer> messages)
        {
        }

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!;
            return false;
        }

        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => throw new NotSupportedException();

        public bool IsUnderPressure() => false;
    }
}
