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
public sealed class RecoveryCacheMemoryTests
{
    private static readonly StreamId _stream = StreamId.Create("memory", "partition");

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Cache_RequiresPositiveByteBudget(long budget)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateCache(budget));
        Assert.Equal("maxCacheSizeBytes", exception.ParamName);
    }

    [Fact]
    public void Cache_ByteBudgetBlocksOrderedSuffixWithoutRepeatedPacking()
    {
        var adapter = new DataAdapter();
        var pool = new BufferPool(128);
        using var cache = CreateCache(80, adapter, pool);
        var messages = new[] { Record(1, 24), Record(2, 40), Record(3, 24) };

        var admitted = cache.Add(messages, DateTime.UnixEpoch);

        Assert.Equal([1L, 2L], Sequences(admitted));
        Assert.Equal(64, cache.SizeInBytes);
        Assert.Equal(2, cache.ItemCount);
        Assert.Equal(0, cache.GetMaxAddCount());
        Assert.True(cache.IsUnderPressure());
        Assert.Equal(3, adapter.PackCount);
        Assert.Equal(1, pool.AllocationCount);
        for (var i = 0; i < 3; i++)
        {
            Assert.Empty(cache.Add([messages[2]], DateTime.UnixEpoch));
            Assert.False(cache.TryPurgeFromCache(out _));
        }

        Assert.Equal(3, adapter.PackCount);
        Assert.Equal(1, pool.AllocationCount);
        Assert.Equal(64, cache.SizeInBytes);
        AssertPayload(cache, messages[0]);
        AssertPayload(cache, messages[1]);

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);

        Assert.Equal(40, cache.SizeInBytes);
        Assert.Equal(1000, cache.GetMaxAddCount());
        Assert.Equal([3L], Sequences(cache.Add([messages[2]], DateTime.UnixEpoch)));
        Assert.Equal(64, cache.SizeInBytes);
        AssertPayload(cache, messages[1]);
        AssertPayload(cache, messages[2]);
    }

    [Fact]
    public void Cache_PartialWatermarkKeepsPressureUntilNextRecordFits()
    {
        using var cache = CreateCache(80);
        var messages = new[] { Record(1, 24), Record(2, 40), Record(3, 64) };
        Assert.Equal([1L, 2L], Sequences(cache.Add(messages, DateTime.UnixEpoch)));

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);

        Assert.Equal(40, cache.SizeInBytes);
        Assert.Equal(0, cache.GetMaxAddCount());
        Assert.Empty(cache.Add([messages[2]], DateTime.UnixEpoch));
        AssertPayload(cache, messages[1]);

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(2), DateTime.UnixEpoch);

        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal(1000, cache.GetMaxAddCount());
        Assert.Equal([3L], Sequences(cache.Add([messages[2]], DateTime.UnixEpoch)));
        Assert.Equal(64, cache.SizeInBytes);
        AssertPayload(cache, messages[2]);
    }

    [Fact]
    public void Cache_RecordCapacityAlsoBoundsAdmission()
    {
        using var cache = CreateCache(128, maxCount: 1);
        var messages = new[] { Record(1, 16), Record(2, 16) };

        Assert.Equal([1L], Sequences(cache.Add(messages, DateTime.UnixEpoch)));
        Assert.Equal(16, cache.SizeInBytes);
        Assert.Equal(0, cache.GetMaxAddCount());
        Assert.Empty(cache.Add([messages[1]], DateTime.UnixEpoch));

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);

        Assert.Equal(1, cache.GetMaxAddCount());
        Assert.Equal([2L], Sequences(cache.Add([messages[1]], DateTime.UnixEpoch)));
        AssertPayload(cache, messages[1]);
    }

    [Fact]
    public void Cache_EmptyCacheAdmitsOneOversizedRecordThenWaitsForDelivery()
    {
        var pool = new BufferPool(32);
        var eviction = new EvictionStrategy();
        using var cache = CreateCache(16, pool: pool, eviction: eviction);
        var large = Record(1, 96);
        var small = Record(2, 16);

        Assert.Equal([1L], Sequences(cache.Add([large, small], DateTime.UnixEpoch)));
        Assert.Equal(96, cache.SizeInBytes);
        Assert.Equal(96, eviction.OwnedBytes);
        Assert.Equal(1, eviction.OwnedCount);
        Assert.Equal(0, pool.OwnedCount);
        Assert.Equal(0, cache.GetMaxAddCount());
        AssertPayload(cache, large);

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);

        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal(0, eviction.OwnedCount);
        Assert.Equal([2L], Sequences(cache.Add([small], DateTime.UnixEpoch)));
        Assert.Equal(16, cache.SizeInBytes);
        Assert.Equal(32, eviction.OwnedBytes);
        AssertPayload(cache, small);
    }

    [Fact]
    public void Cache_FailedPackingRestoresBytesAndPartiallyUsedBuffer()
    {
        var pool = new BufferPool(64);
        var adapter = new DataAdapter();
        var eviction = new EvictionStrategy();
        using var cache = CreateCache(200, adapter, pool, eviction);
        var first = Record(1, 16);
        _ = cache.Add([first], DateTime.UnixEpoch);
        adapter.FailSequence = 3;

        Assert.Throws<InvalidOperationException>(() => cache.Add(
            [Record(2, 32), Record(3, 64)], DateTime.UnixEpoch));

        Assert.Equal(16, cache.SizeInBytes);
        Assert.Equal(1, cache.ItemCount);
        Assert.Equal(2, pool.AllocationCount);
        Assert.Equal(1, pool.FreeCount);
        Assert.Equal(1, pool.OwnedCount);
        Assert.Equal(1, eviction.OwnedCount);
        Assert.Equal(1000, cache.GetMaxAddCount());
        AssertPayload(cache, first);

        adapter.FailSequence = null;
        var retry = Record(2, 48);
        Assert.Equal([2L], Sequences(cache.Add([retry], DateTime.UnixEpoch)));
        Assert.Equal(64, cache.SizeInBytes);
        Assert.Equal(2, pool.AllocationCount);
        AssertPayload(cache, first);
        AssertPayload(cache, retry);
    }

    [Fact]
    public void Cache_ChronologicalPurgeReleasesByteAccounting()
    {
        var pool = new BufferPool(128);
        using var cache = CreateCache(256, pool: pool, eviction: new EvictionStrategy(TimeSpan.Zero));
        _ = cache.Add([Record(1, 32)], DateTime.UnixEpoch);
        var retained = Record(2, 64);
        _ = cache.Add([retained], DateTime.UnixEpoch.AddSeconds(1));

        Assert.False(cache.TryPurgeFromCache(out _));

        Assert.Equal(1, cache.ItemCount);
        Assert.Equal(64, cache.SizeInBytes);
        Assert.Equal("1", cache.LastPurgedOffset);
        Assert.Equal(1, pool.OwnedCount);
        AssertPayload(cache, retained);

        cache.UpdateDeliveryProgress(new EventSequenceTokenV2(2), DateTime.UnixEpoch.AddSeconds(2));

        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal("2", cache.LastPurgedOffset);
        Assert.Equal(0, pool.OwnedCount);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void Cache_MultiplePartitionsRespectIndependentByteBudgets()
    {
        const int mebibyte = 1024 * 1024;
        const int budget = 2 * mebibyte;
        var caches = Enumerable.Range(0, 3)
            .Select(_ => CreateCache(budget, pool: new BufferPool(mebibyte)))
            .ToArray();
        var records = Enumerable.Range(0, caches.Length)
            .Select(partition => new[]
            {
                Record(1, mebibyte / 4, partition),
                Record(2, mebibyte + mebibyte / 4, partition),
                Record(3, mebibyte / 4, partition),
                Record(4, mebibyte / 2, partition),
            })
            .ToArray();
        try
        {
            for (var i = 0; i < caches.Length; i++)
            {
                Assert.Equal([1L, 2L, 3L], Sequences(caches[i].Add(records[i], DateTime.UnixEpoch)));
                Assert.Equal(7L * mebibyte / 4, caches[i].SizeInBytes);
                Assert.True(caches[i].IsUnderPressure());
                AssertPayload(caches[i], records[i][1]);
            }

            Assert.Equal(21L * mebibyte / 4, caches.Sum(cache => cache.SizeInBytes));
            for (var i = 1; i < caches.Length; i++)
            {
                caches[i].UpdateDeliveryProgress(new EventSequenceTokenV2(3), DateTime.UnixEpoch);
                Assert.Equal([4L], Sequences(caches[i].Add([records[i][3]], DateTime.UnixEpoch)));
                Assert.Equal(mebibyte / 2, caches[i].SizeInBytes);
                Assert.False(caches[i].IsUnderPressure());
                AssertPayload(caches[i], records[i][3]);
            }

            Assert.Equal(11L * mebibyte / 4, caches.Sum(cache => cache.SizeInBytes));
            Assert.True(caches[0].IsUnderPressure());
            Assert.Equal(3, caches[0].ItemCount);
            AssertPayload(caches[0], records[0][0]);
            AssertPayload(caches[0], records[0][2]);
        }
        finally
        {
            foreach (var cache in caches)
            {
                cache.Dispose();
            }
        }

        Assert.All(caches, cache => Assert.Equal(0, cache.SizeInBytes));
    }

    [Fact]
    public void Cache_RepeatedFillDrainReusesBuffersAndPreservesDeliveredCopies()
    {
        var pool = new BufferPool(64);
        var eviction = new EvictionStrategy();
        using var cache = CreateCache(64, pool: pool, eviction: eviction);
        var snapshots = new List<(TestRecord Record, byte[] Payload)>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var first = Record(cycle * 2 + 1, 16);
            var second = Record(cycle * 2 + 2, 24);
            _ = cache.Add([first, second], DateTime.UnixEpoch);
            Assert.Equal(40, cache.SizeInBytes);
            using var cursor = cache.GetCacheCursor(first.StreamId, new EventSequenceTokenV2(first.Sequence));
            Assert.True(cursor.MoveNext());
            snapshots.Add((first, Assert.IsType<Batch>(cursor.GetCurrent(out _)).Payload));

            cache.UpdateDeliveryProgress(new EventSequenceTokenV2(second.Sequence), DateTime.UnixEpoch);

            Assert.Equal(0, cache.SizeInBytes);
            Assert.Equal(0, pool.OwnedCount);
            Assert.Equal(0, eviction.OwnedCount);
        }

        Assert.Equal(1, pool.CreatedCount);
        Assert.Equal(5, pool.AllocationCount);
        Assert.Equal(5, pool.FreeCount);
        foreach (var (record, payload) in snapshots)
        {
            Assert.Equal(record.Payload, payload);
        }
    }

    [Fact]
    public async Task Receiver_StagesOneBatchAndAcknowledgesOnlyAdmittedPrefixes()
    {
        var source = new Source([40, 40, 24, 32]);
        var adapter = new DataAdapter();
        using var cache = CreateCache(64, adapter);
        var checkpoint = new Checkpointer();
        var receiver = new RecoverableStreamReceiver<TestRecord>(source, adapter, cache, checkpoint, false);

        Assert.Equal([1L], Sequences(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None)));
        var staging = Assert.Single(source.AdmissionArrays);
        Assert.Null(staging[0]);
        Assert.Equal([2L, 3L, 4L], staging.Skip(1).Select(record => record.Sequence));
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, source.ReadOffset);
        Assert.Empty(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None));
        Assert.Equal(1, source.ReadCount);

        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);
        Assert.Equal([2L, 3L], Sequences(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None)));
        Assert.Equal(64, cache.SizeInBytes);
        Assert.Equal(3, source.ReadOffset);
        Assert.Equal("1", checkpoint.Offset);
        Assert.All(staging.Take(3), Assert.Null);
        Assert.Equal(4, staging[3].Sequence);

        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(2), DateTime.UnixEpoch);
        Assert.Equal([4L], Sequences(await receiver.GetQueueMessagesAsync(1, CancellationToken.None)));

        Assert.Equal(56, cache.SizeInBytes);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal([1000], source.RequestedCounts);
        Assert.Equal([1L, 2L, 3L, 4L], source.Admitted.SelectMany(batch => batch));
        Assert.Equal([1, 2, 1], source.Admitted.Select(batch => batch.Length));
        Assert.Equal(4, source.ReadOffset);
        Assert.Equal("2", checkpoint.Offset);
        Assert.All(staging, Assert.Null);
        Assert.Empty(source.Failed);
        await receiver.Shutdown(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Receiver_StagedPackingFailureClearsSuffixAndReplaysAfterAdmittedOffset()
    {
        var source = new Source([40, 40, 16, 32]);
        var adapter = new DataAdapter();
        var pool = new BufferPool(64);
        using var cache = CreateCache(64, adapter, pool);
        var receiver = new RecoverableStreamReceiver<TestRecord>(source, adapter, cache, new Checkpointer(), false);
        Assert.Equal([1L], Sequences(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None)));
        var firstStaging = Assert.Single(source.AdmissionArrays);
        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);
        adapter.FailSequence = 3;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receiver.GetQueueMessagesAsync(1000, CancellationToken.None));

        Assert.Equal([2L, 3L, 4L], Assert.Single(source.Failed));
        Assert.All(firstStaging, Assert.Null);
        Assert.Equal(1, source.ReadOffset);
        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(0, pool.OwnedCount);
        Assert.Equal(1000, receiver.GetMaxAddCount());

        adapter.FailSequence = null;
        Assert.Equal([2L, 3L], Sequences(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None)));
        Assert.Equal(2, source.ReadCount);
        Assert.Equal(56, cache.SizeInBytes);
        AssertPayload(cache, Record(2, 40));
        AssertPayload(cache, Record(3, 16));
        receiver.UpdateDeliveryProgress(new EventSequenceTokenV2(3), DateTime.UnixEpoch);
        Assert.Equal([4L], Sequences(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None)));
        Assert.Equal([1L, 2L, 3L, 4L], source.Admitted.SelectMany(batch => batch));
        Assert.Equal(2, source.ReadCount);
        await receiver.Shutdown(Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receiver_ShutdownClearsPendingPayloadRootsAndCacheOwnership(bool failShutdown)
    {
        var source = new Source([40, 40]) { FailShutdown = failShutdown };
        var adapter = new DataAdapter();
        var pool = new BufferPool(64);
        var eviction = new EvictionStrategy();
        using var cache = CreateCache(64, adapter, pool, eviction);
        var checkpoint = new Checkpointer();
        var receiver = new RecoverableStreamReceiver<TestRecord>(source, adapter, cache, checkpoint, false);
        Assert.Single(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None));
        var staging = Assert.Single(source.AdmissionArrays);
        Assert.Null(staging[0]);
        Assert.Equal(2, staging[1].Sequence);

        if (failShutdown)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Shutdown(Timeout.InfiniteTimeSpan));
        }
        else
        {
            await receiver.Shutdown(Timeout.InfiniteTimeSpan);
        }

        Assert.All(staging, Assert.Null);
        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal(0, cache.ItemCount);
        Assert.Equal(0, pool.OwnedCount);
        Assert.Equal(0, eviction.OwnedCount);
        Assert.Equal(1, checkpoint.FlushCount);
        Assert.True(source.Stopped);
        Assert.Empty(await receiver.GetQueueMessagesAsync(1000, CancellationToken.None));
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Receiver_ShutdownPreventsLateSourceBatchFromRepopulatingCache()
    {
        var source = new DelayedSource();
        var adapter = new DataAdapter();
        var pool = new BufferPool(64);
        using var cache = CreateCache(64, adapter, pool);
        var receiver = new RecoverableStreamReceiver<TestRecord>(source, adapter, cache, new Checkpointer(), false);
        var read = receiver.GetQueueMessagesAsync(1000, CancellationToken.None);
        await source.Started.Task;

        await receiver.Shutdown(Timeout.InfiniteTimeSpan);
        Assert.True(source.ReadCancellation.IsCancellationRequested);
        source.Completion.SetResult([Record(1, 40), Record(2, 40)]);

        Assert.Empty(await read);
        Assert.Equal(0, adapter.PackCount);
        Assert.Equal(0, cache.SizeInBytes);
        Assert.Equal(0, pool.AllocationCount);
        Assert.Equal(0, source.AdmissionCount);
    }

    [Fact]
    public async Task Receiver_AdmitsFullReadWhenByteBudgetFits()
    {
        var source = new Source(Enumerable.Repeat(16, 1000).ToArray());
        var adapter = new DataAdapter();
        var pool = new BufferPool(16_000);
        using var cache = CreateCache(16_000, adapter, pool);
        var receiver = new RecoverableStreamReceiver<TestRecord>(source, adapter, cache, new Checkpointer(), false);

        var notifications = await receiver.GetQueueMessagesAsync(1000, CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, 1000).Select(sequence => (long)sequence), Sequences(notifications));
        Assert.Equal(16_000, cache.SizeInBytes);
        Assert.Equal(1000, adapter.PackCount);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1000, Assert.Single(source.Admitted).Length);
        Assert.Equal(1, pool.AllocationCount);
        Assert.All(Assert.Single(source.AdmissionArrays), Assert.Null);
        await receiver.Shutdown(Timeout.InfiniteTimeSpan);
    }

    private static RecoverableStreamQueueCache<TestRecord> CreateCache(
        long budget,
        DataAdapter? adapter = null,
        BufferPool? pool = null,
        EvictionStrategy? eviction = null,
        int maxCount = 4096)
        => new(
            1000,
            pool ?? new BufferPool(128),
            adapter ?? new DataAdapter(),
            eviction ?? new EvictionStrategy(),
            NullLogger.Instance,
            maxCacheSizeBytes: budget,
            maxCacheSize: maxCount);

    private static TestRecord Record(long sequence, int encodedSize, int partition = 0)
    {
        var payload = new byte[encodedSize - sizeof(int)];
        payload.AsSpan().Fill((byte)(sequence % 251));
        var stream = partition == 0 ? _stream : StreamId.Create("memory", $"partition-{partition}");
        return new(stream, sequence, payload);
    }

    private static IEnumerable<long> Sequences(IReadOnlyList<StreamPosition> positions)
        => positions.Select(position => position.SequenceToken.SequenceNumber);

    private static IEnumerable<long> Sequences(IList<IBatchContainer> batches)
        => batches.Select(batch => batch.SequenceToken.SequenceNumber);

    private static void AssertPayload(RecoverableStreamQueueCache<TestRecord> cache, TestRecord record)
    {
        using var cursor = cache.GetCacheCursor(record.StreamId, new EventSequenceTokenV2(record.Sequence));
        Assert.True(cursor.MoveNext());
        var batch = Assert.IsType<Batch>(cursor.GetCurrent(out var exception));
        Assert.Null(exception);
        Assert.Equal(record.StreamId, batch.StreamId);
        Assert.Equal(record.Sequence, batch.SequenceToken.SequenceNumber);
        Assert.Equal(record.Payload, batch.Payload);
    }

    private sealed record TestRecord(StreamId StreamId, long Sequence, byte[] Payload);

    private sealed class DataAdapter : IRecoverableStreamDataAdapter<TestRecord>
    {
        public int PackCount { get; private set; }
        public long? FailSequence { get; set; }

        public StreamPosition GetStreamPosition(TestRecord message)
            => new(message.StreamId, new EventSequenceTokenV2(message.Sequence));

        public CachedMessage FromQueueMessage(
            StreamPosition position,
            TestRecord message,
            DateTime dequeueTimeUtc,
            Func<int, ArraySegment<byte>> getSegment)
        {
            PackCount++;
            var segment = getSegment(SegmentBuilder.CalculateAppendSize(message.Payload));
            var offset = 0;
            SegmentBuilder.Append(segment, ref offset, message.Payload);
            if (message.Sequence == FailSequence)
            {
                throw new InvalidOperationException("Packing failed after writing the record.");
            }

            return new()
            {
                StreamId = position.StreamId,
                SequenceNumber = message.Sequence,
                Segment = segment,
                EnqueueTimeUtc = dequeueTimeUtc,
                DequeueTimeUtc = dequeueTimeUtc,
            };
        }

        public IBatchContainer GetBatchContainer(ref CachedMessage message)
        {
            var offset = 0;
            return new Batch(
                message.StreamId,
                GetSequenceToken(ref message),
                SegmentBuilder.ReadNextBytes(message.Segment, ref offset).ToArray());
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage message)
            => new EventSequenceTokenV2(message.SequenceNumber, message.EventIndex);

        public string GetOffset(ref CachedMessage message)
            => message.SequenceNumber.ToString(CultureInfo.InvariantCulture);

        public bool TryGetOffset(StreamSequenceToken token, out string offset)
        {
            offset = token.SequenceNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    private sealed class Batch(StreamId streamId, StreamSequenceToken token, byte[] payload) : IBatchContainer
    {
        public StreamId StreamId => streamId;
        public StreamSequenceToken SequenceToken => token;
        public byte[] Payload => payload;
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
    }

    private sealed class Source(int[] encodedSizes) : IRecoverableStreamSource<TestRecord>
    {
        public List<int> RequestedCounts { get; } = [];
        public List<long[]> Admitted { get; } = [];
        public List<long[]> Failed { get; } = [];
        public List<TestRecord[]> AdmissionArrays { get; } = [];
        public long ReadOffset { get; private set; }
        public int ReadCount { get; private set; }
        public bool Stopped { get; private set; }
        public bool FailShutdown { get; init; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TestRecord>> Read(int maxCount, CancellationToken cancellationToken)
        {
            ReadCount++;
            RequestedCounts.Add(maxCount);
            return Task.FromResult<IReadOnlyList<TestRecord>>(encodedSizes
                .Select((size, index) => (size, sequence: index + 1))
                .Where(item => item.sequence > ReadOffset)
                .Take(maxCount)
                .Select(item => Record(item.sequence, item.size))
                .ToArray());
        }

        public void MessagesAdded(IReadOnlyList<TestRecord> messages)
        {
            Admitted.Add(messages.Select(message => message.Sequence).ToArray());
            AdmissionArrays.Add(Assert.IsType<ArraySegment<TestRecord>>(messages).Array!);
            ReadOffset = messages[^1].Sequence;
        }

        public void MessagesAddFailed(IReadOnlyList<TestRecord> messages)
            => Failed.Add(messages.Select(message => message.Sequence).ToArray());

        public Task Shutdown(CancellationToken cancellationToken)
        {
            Stopped = true;
            return FailShutdown ? Task.FromException(new InvalidOperationException("Shutdown failed.")) : Task.CompletedTask;
        }
    }

    private sealed class DelayedSource : IRecoverableStreamSource<TestRecord>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<TestRecord>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadCancellation { get; private set; }
        public int AdmissionCount { get; private set; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TestRecord>> Read(int maxCount, CancellationToken cancellationToken)
        {
            ReadCancellation = cancellationToken;
            Started.SetResult();
            return Completion.Task;
        }

        public void MessagesAdded(IReadOnlyList<TestRecord> messages) => AdmissionCount++;
        public Task Shutdown(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Checkpointer : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => false;
        public string? Offset { get; private set; }
        public int FlushCount { get; private set; }
        public Task<string> Load() => Task.FromResult(string.Empty);
        public Task<string> Load(CancellationToken cancellationToken) => Load();
        public void Update(string offset, DateTime utcNow) => Offset = offset;
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken) => Offset = offset;
        public Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BufferPool(int bufferSize) : IObjectPool<FixedSizeBuffer>
    {
        private readonly Stack<FixedSizeBuffer> _available = new();
        private readonly HashSet<FixedSizeBuffer> _owned = [];
        public int CreatedCount { get; private set; }
        public int AllocationCount { get; private set; }
        public int FreeCount { get; private set; }
        public int OwnedCount => _owned.Count;

        public FixedSizeBuffer Allocate()
        {
            AllocationCount++;
            if (!_available.TryPop(out var buffer))
            {
                buffer = new FixedSizeBuffer(bufferSize);
                CreatedCount++;
            }

            Assert.True(_owned.Add(buffer));
            buffer.Pool = this;
            return buffer;
        }

        public void Free(FixedSizeBuffer resource)
        {
            Assert.True(_owned.Remove(resource));
            FreeCount++;
            _available.Push(resource);
        }
    }

    private sealed class EvictionStrategy(TimeSpan? retention = null)
        : ChronologicalEvictionStrategy(
            NullLogger.Instance,
            new TimePurgePredicate(retention ?? TimeSpan.MaxValue, retention ?? TimeSpan.MaxValue),
            cacheMonitor: null,
            monitorWriteInterval: null)
    {
        public int OwnedCount => inUseBuffers.Count;
        public long OwnedBytes => inUseBuffers.Sum(buffer => (long)buffer.SizeInByte);
    }
}
