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

        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(11), DateTime.UtcNow);
        Assert.Equal("11", checkpointer.LastUpdatedCheckpoint);

        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checkpointer.FlushCount);
        Assert.True(source.IsShutdown);
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

    private sealed record TestQueueMessage(StreamId StreamId, long SequenceNumber, string Payload);

    private sealed class TestDataAdapter : IRecoverableStreamDataAdapter<TestQueueMessage>
    {
        public int CompareCallCount { get; private set; }

        public StreamPosition GetStreamPosition(TestQueueMessage queueMessage)
            => new(queueMessage.StreamId, new EventSequenceTokenV2(queueMessage.SequenceNumber));

        public CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            TestQueueMessage queueMessage,
            DateTime dequeueTimeUtc,
            Func<int, ArraySegment<byte>> getSegment)
        {
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

    private sealed class TestSource(IReadOnlyList<TestQueueMessage> messages) : IRecoverableStreamSource<TestQueueMessage>
    {
        private bool read;

        public RecoverableStreamStartPosition StartPosition { get; private set; }

        public bool IsShutdown { get; private set; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpEvictionStrategy : IEvictionStrategy
    {
        public IPurgeObservable PurgeObservable { private get; set; } = null!;

        public Action<CachedMessage?, CachedMessage?>? OnPurged { get; set; }

        public void PerformPurge(DateTime utcNow)
        {
        }

        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
        }
    }

    private sealed class TrackingBufferPool : IObjectPool<FixedSizeBuffer>
    {
        public int FreeCount { get; private set; }

        public FixedSizeBuffer Allocate() => new(4 * 1024) { Pool = this };

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
