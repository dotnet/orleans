using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT")]
public sealed class EncodedOffsetPooledQueueCacheTests
{
    [Fact]
    public void CachedMessageBlock_AdapterAwareSearchUsesEncodedOffset()
    {
        var adapter = new EncodedOffsetDataAdapter();
        var block = new CachedMessageBlock(3);
        block.Add(CreateMessage(default, "001"));
        block.Add(CreateMessage(default, "003"));
        block.Add(CreateMessage(default, "005"));

        Assert.Equal(1, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EncodedOffsetToken("003"), adapter));
        Assert.Equal(1, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EncodedOffsetToken("004"), adapter));
        Assert.Equal(2, block.GetIndexOfFirstMessageLessThanOrEqualTo(new EncodedOffsetToken("005"), adapter));
        Assert.True(adapter.CompareCallCount >= 4);
    }

    [Fact]
    public void TypedCursorAcquisition_UsesEncodedOffsetsWhenNumericFieldsDiffer()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter(useNumericPrefix: false);
        var cache = CreateCache(adapter);
        var first = CreateMessage(streamId, "010");
        first.SequenceNumber = 100;
        var second = CreateMessage(streamId, "020");
        second.SequenceNumber = 200;
        cache.Add([first, second], DateTime.UnixEpoch);

        var result = cache.TryGetCursor(streamId, new EncodedOffsetToken("020"));

        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.Null(result.CacheMiss);
        var move = cache.TryGetNextMessageWithResult(result.Cursor!, out var batch);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, move.Kind);
        Assert.NotNull(batch);
        Assert.Equal("020", Assert.IsType<EncodedOffsetToken>(batch.SequenceToken).Offset);
        Assert.Equal(1, adapter.GetBatchContainerCallCount);
    }

    [Fact]
    public void Cursor_AfterNewestWaitsUntilExternalOffsetArrives()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        Add(cache, streamId, "010", "020");
        var cursorResult = cache.TryGetCursor(streamId, new EncodedOffsetToken("030"));
        Assert.Equal(QueueCacheCursorResultKind.Success, cursorResult.Kind);
        Assert.Null(cursorResult.CacheMiss);
        Assert.NotNull(cursorResult.Cursor);
        var cursor = cursorResult.Cursor;

        var waiting = cache.TryGetNextMessageWithResult(cursor, out var message);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, waiting.Kind);
        Assert.Null(waiting.CacheMiss);
        Assert.Null(message);
        Assert.Equal(0, adapter.GetBatchContainerCallCount);

        Add(cache, streamId, "030");

        var next = cache.TryGetNextMessageWithResult(cursor, out message);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, next.Kind);
        Assert.Null(next.CacheMiss);
        var batch = Assert.IsType<TestBatchContainer>(message);
        Assert.Equal(streamId, batch.StreamId);
        Assert.Equal("030", Assert.IsType<EncodedOffsetToken>(batch.SequenceToken).Offset);
        var exhausted = cache.TryGetNextMessageWithResult(cursor, out message);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, exhausted.Kind);
        Assert.Null(exhausted.CacheMiss);
        Assert.Null(message);
        Assert.True(adapter.CompareCallCount > 0);
        Assert.Equal(1, adapter.GetBatchContainerCallCount);
    }

    [Fact]
    public void Cursor_UsesExternalOffsetAcrossMessageBlocks()
    {
        const int defaultBlockSize = 16 * 1024;
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        var messages = Enumerable.Range(0, defaultBlockSize + 2)
            .Select(index => CreateMessage(
                streamId,
                index.ToString("D5", CultureInfo.InvariantCulture)))
            .ToList();
        cache.Add(messages, DateTime.UnixEpoch);
        var requested = (defaultBlockSize - 1).ToString("D5", CultureInfo.InvariantCulture);

        var cursorResult = cache.TryGetCursor(streamId, new EncodedOffsetToken(requested));
        Assert.Equal(QueueCacheCursorResultKind.Success, cursorResult.Kind);
        Assert.Null(cursorResult.CacheMiss);
        Assert.NotNull(cursorResult.Cursor);
        var cursor = cursorResult.Cursor;

        var firstResult = cache.TryGetNextMessageWithResult(cursor, out var first);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, firstResult.Kind);
        Assert.Null(firstResult.CacheMiss);
        Assert.NotNull(first);
        Assert.Equal(streamId, first.StreamId);
        Assert.Equal(requested, Assert.IsType<EncodedOffsetToken>(first.SequenceToken).Offset);
        var secondResult = cache.TryGetNextMessageWithResult(cursor, out var second);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, secondResult.Kind);
        Assert.Null(secondResult.CacheMiss);
        Assert.NotNull(second);
        Assert.Equal(streamId, second.StreamId);
        Assert.Equal(
            defaultBlockSize.ToString("D5", CultureInfo.InvariantCulture),
            Assert.IsType<EncodedOffsetToken>(second.SequenceToken).Offset);
        Assert.True(adapter.CompareCallCount >= 4);
        Assert.Equal(2, adapter.GetBatchContainerCallCount);
    }

    [Fact]
    public void Cursor_WhenExternalPositionWasPurgedReturnsCacheMiss()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        Add(cache, streamId, "010", "020", "030");
        var cursorResult = cache.TryGetCursor(streamId, new EncodedOffsetToken("010"));
        Assert.Equal(QueueCacheCursorResultKind.Success, cursorResult.Kind);
        Assert.Null(cursorResult.CacheMiss);
        Assert.NotNull(cursorResult.Cursor);
        cache.RemoveOldestMessage();

        var result = cache.TryGetNextMessageWithResult(cursorResult.Cursor, out var message);

        Assert.Equal(QueueCacheCursorMoveResultKind.CacheMiss, result.Kind);
        Assert.Null(message);
        var cacheMiss = Assert.NotNull(result.CacheMiss);
        Assert.Equal("010", Assert.IsType<EncodedOffsetToken>(cacheMiss.RequestedToken).Offset);
        Assert.Equal("020", Assert.IsType<EncodedOffsetToken>(cacheMiss.LowToken).Offset);
        Assert.Equal("030", Assert.IsType<EncodedOffsetToken>(cacheMiss.HighToken).Offset);
        Assert.Equal(new EncodedOffsetToken("010").ToString(), cacheMiss.Requested);
        Assert.Equal(new EncodedOffsetToken("020").ToString(), cacheMiss.Low);
        Assert.Equal(new EncodedOffsetToken("030").ToString(), cacheMiss.High);
        Assert.Equal(0, adapter.GetBatchContainerCallCount);
    }

    [Fact]
    public void Cursor_BeforeOldestExternalOffsetReturnsCacheMiss()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        Add(cache, streamId, "020", "030");
        var requestedToken = new EncodedOffsetToken("010");

        var result = cache.TryGetCursor(streamId, requestedToken);

        Assert.Equal(QueueCacheCursorResultKind.CacheMiss, result.Kind);
        Assert.Null(result.Cursor);
        var cacheMiss = Assert.NotNull(result.CacheMiss);
        Assert.Same(requestedToken, cacheMiss.RequestedToken);
        Assert.Equal("020", Assert.IsType<EncodedOffsetToken>(cacheMiss.LowToken).Offset);
        Assert.Equal("030", Assert.IsType<EncodedOffsetToken>(cacheMiss.HighToken).Offset);
        Assert.Equal(requestedToken.ToString(), cacheMiss.Requested);
        Assert.Equal(new EncodedOffsetToken("020").ToString(), cacheMiss.Low);
        Assert.Equal(new EncodedOffsetToken("030").ToString(), cacheMiss.High);
        Assert.Equal(0, adapter.GetBatchContainerCallCount);
    }

    private static PooledQueueCache CreateCache(EncodedOffsetDataAdapter adapter)
        => new(adapter, NullLogger.Instance, cacheMonitor: null, cacheMonitorWriteInterval: null);

    private static void Add(
        PooledQueueCache cache,
        StreamId streamId,
        params string[] offsets)
        => cache.Add(
            offsets.Select(offset => CreateMessage(streamId, offset)).ToList(),
            DateTime.UnixEpoch);

    private static CachedMessage CreateMessage(StreamId streamId, string offset)
    {
        var bytes = new byte[SegmentBuilder.CalculateAppendSize(offset)];
        var segment = new ArraySegment<byte>(bytes);
        var writeOffset = 0;
        SegmentBuilder.Append(segment, ref writeOffset, offset);
        return new CachedMessage
        {
            StreamId = streamId,
            SequenceNumber = EncodedOffsetToken.SharedSequenceNumber,
            EventIndex = 0,
            EnqueueTimeUtc = DateTime.UnixEpoch,
            DequeueTimeUtc = DateTime.UnixEpoch,
            Segment = segment,
        };
    }

    private sealed class EncodedOffsetDataAdapter(bool useNumericPrefix = true) : ICacheDataAdapter
    {
        public int CompareCallCount { get; private set; }
        public int GetBatchContainerCallCount { get; private set; }

        public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            GetBatchContainerCallCount++;
            return new TestBatchContainer(
                cachedMessage.StreamId,
                GetSequenceToken(ref cachedMessage));
        }

        public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            => new EncodedOffsetToken(ReadOffset(ref cachedMessage));

        public int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
        {
            CompareCallCount++;
            var numericComparison = useNumericPrefix ? cachedMessage.Compare(token) : 0;
            if (numericComparison != 0)
            {
                return numericComparison;
            }

            return string.CompareOrdinal(
                ReadOffset(ref cachedMessage),
                Assert.IsType<EncodedOffsetToken>(token).Offset);
        }

        private static string ReadOffset(ref CachedMessage cachedMessage)
        {
            var readOffset = 0;
            return SegmentBuilder.ReadNextString(cachedMessage.Segment, ref readOffset)!;
        }
    }

    private sealed class EncodedOffsetToken(string offset) : StreamSequenceToken
    {
        public const long SharedSequenceNumber = 42;

        public string Offset { get; } = offset;

        public override long SequenceNumber { get; protected set; } = SharedSequenceNumber;

        public override int EventIndex { get; protected set; }

        public override bool Equals(StreamSequenceToken? other)
            => other is EncodedOffsetToken token && string.Equals(Offset, token.Offset, StringComparison.Ordinal);

        public override int CompareTo(StreamSequenceToken? other)
            => other is null
                ? 1
                : string.CompareOrdinal(Offset, Assert.IsType<EncodedOffsetToken>(other).Offset);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Offset);

        public override string ToString() => $"EncodedOffset({Offset})";
    }

    private sealed class TestBatchContainer(
        StreamId streamId,
        StreamSequenceToken sequenceToken) : IBatchContainer
    {
        public StreamId StreamId { get; } = streamId;

        public StreamSequenceToken SequenceToken { get; } = sequenceToken;

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];

        public bool ImportRequestContext() => false;
    }
}
