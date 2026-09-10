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
    public void Cursor_AfterNewestWaitsUntilExternalOffsetArrives()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        Add(cache, streamId, "010", "020");
        var cursor = cache.GetCursor(streamId, new EncodedOffsetToken("030"));

        Assert.False(cache.TryGetNextMessage(cursor, out _));
        Assert.Equal(0, adapter.GetBatchContainerCallCount);

        Add(cache, streamId, "030");

        Assert.True(cache.TryGetNextMessage(cursor, out var batch));
        Assert.Equal("030", Assert.IsType<EncodedOffsetToken>(batch.SequenceToken).Offset);
        Assert.False(cache.TryGetNextMessage(cursor, out _));
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

        var cursor = cache.GetCursor(streamId, new EncodedOffsetToken(requested));

        Assert.True(cache.TryGetNextMessage(cursor, out var first));
        Assert.Equal(requested, Assert.IsType<EncodedOffsetToken>(first.SequenceToken).Offset);
        Assert.True(cache.TryGetNextMessage(cursor, out var second));
        Assert.Equal(
            defaultBlockSize.ToString("D5", CultureInfo.InvariantCulture),
            Assert.IsType<EncodedOffsetToken>(second.SequenceToken).Offset);
        Assert.True(adapter.CompareCallCount >= 4);
    }

    [Fact]
    public void Cursor_WhenExternalPositionWasPurgedThrowsCacheMiss()
    {
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var adapter = new EncodedOffsetDataAdapter();
        var cache = CreateCache(adapter);
        Add(cache, streamId, "010", "020");
        var cursor = cache.GetCursor(streamId, new EncodedOffsetToken("010"));
        cache.RemoveOldestMessage();

        var exception = Assert.Throws<QueueCacheMissException>(
            () => cache.TryGetNextMessage(cursor, out _));

        Assert.Equal(new EncodedOffsetToken("010").ToString(), exception.Requested);
        Assert.Equal(new EncodedOffsetToken("020").ToString(), exception.Low);
        Assert.Equal(new EncodedOffsetToken("020").ToString(), exception.High);
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

    private sealed class EncodedOffsetDataAdapter : ICacheDataAdapter
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
            var numericComparison = cachedMessage.Compare(token);
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
