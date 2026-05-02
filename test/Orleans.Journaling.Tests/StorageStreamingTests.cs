using System.Buffers;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class StorageStreamingTests
{
    [Fact]
    public async Task VolatileStorage_AppendAndReplaceStoreRawBuffers()
    {
        var storage = new VolatileLogStorage();
        var segmentBytes = new byte[] { 1, 2, 3, 4 };
        var snapshotBytes = new byte[] { 5, 6, 7 };

        using (var segmentData = CreateBuffer(segmentBytes))
        {
            await storage.AppendAsync(segmentData.AsReadOnlySequence(), CancellationToken.None);
        }

        using (var snapshotData = CreateBuffer(snapshotBytes))
        {
            await storage.ReplaceAsync(snapshotData.AsReadOnlySequence(), CancellationToken.None);
        }

        Assert.Single(storage.Segments);
        Assert.Equal(snapshotBytes, storage.Segments[0]);
    }

    [Fact]
    public async Task VolatileStorage_ReadsRawBuffersWithoutFormatDecoding()
    {
        var storage = new VolatileLogStorage();
        var rawBytes = new byte[] { 255, 0, 1 };
        var consumer = new CapturingLogStorageConsumer();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        await storage.ReadAsync(consumer, CancellationToken.None);

        var captured = Assert.Single(consumer.Segments);
        Assert.Equal(rawBytes, captured);
    }

    [Fact]
    public async Task VolatileStorage_InvokesConsumerForEachStoredSegment()
    {
        var storage = new VolatileLogStorage();
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5 };
        var consumer = new CapturingLogStorageConsumer();

        using (var data = CreateBuffer(first))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        using (var data = CreateBuffer(second))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Collection(
            consumer.Segments,
            segment => Assert.Equal(first, segment),
            segment => Assert.Equal(second, segment));
    }

    [Fact]
    public async Task StreamConsumerHelper_BuffersUnconsumedBytesAcrossReads()
    {
        var stream = new ChunkedReadStream([1, 2, 3, 4], chunkSize: 1);
        var consumer = new TwoByteLogStorageConsumer();

        var totalBytesRead = await consumer.ConsumeAsync(stream, CancellationToken.None);

        Assert.Equal(4, totalBytesRead);
        Assert.Equal(4, stream.ReadCount);
        Assert.Collection(
            consumer.Segments,
            segment => Assert.Equal([1, 2], segment),
            segment => Assert.Equal([3, 4], segment));
    }

    [Fact]
    public void SegmentConsumerHelper_BuffersUnconsumedBytesAcrossSegments()
    {
        var consumer = new TwoByteLogStorageConsumer();
        ReadOnlyMemory<byte>[] segments = [new byte[] { 1 }, new byte[] { 2, 3 }, new byte[] { 4 }];

        consumer.Consume(segments);

        Assert.Collection(
            consumer.Segments,
            segment => Assert.Equal([1, 2], segment),
            segment => Assert.Equal([3, 4], segment));
    }

    [Fact]
    public void MemoryConsumerHelper_CompletesEmptyInput()
    {
        var consumer = new CompletionTrackingLogStorageConsumer();

        consumer.Consume(ReadOnlyMemory<byte>.Empty);

        Assert.True(consumer.IsCompleted);
        Assert.Equal(0, consumer.CompletedLength);
    }

    [Fact]
    public void MemoryConsumerHelper_RejectsUnconsumedDataWhenNotCompleted()
    {
        var consumer = new LeavingLogStorageConsumer();

        Assert.Throws<InvalidOperationException>(() => consumer.Consume(new byte[] { 1 }, complete: false));
    }

    [Fact]
    public void BinaryFormatRead_RejectsTruncatedFixed32Frame()
    {
        using var writer = CreateWriter([10, 0, 0, 0, 1, 2]);
        var reader = new ArcBufferReader(writer);
        var consumer = new CapturingLogEntrySink();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: true));

        Assert.Contains("exceeds remaining input bytes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_WaitsForIncompleteFrameWhenInputIsNotCompleted()
    {
        using var writer = CreateWriter([10, 0, 0, 0, 1, 2]);
        var reader = new ArcBufferReader(writer);
        var consumer = new CapturingLogEntrySink();

        var result = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);

        Assert.False(result);
        Assert.Equal(6, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_ConsumesCompletePrefixAndWaitsForPartialSuffix()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetCommittedBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 10, 0]);
        var reader = new ArcBufferReader(data);
        var consumer = new CapturingLogEntrySink();

        var firstResult = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);
        var secondResult = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal([1, 2, 3], entry.Payload);
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(2, reader.Length);
    }

    private static void AppendEntry(LogStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private static ArcBuffer CreateBuffer(ReadOnlySpan<byte> value)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(value);
        return buffer.ConsumeSlice(buffer.Length);
    }

    private static ArcBufferWriter CreateWriter(ReadOnlySpan<byte> value)
    {
        var buffer = new ArcBufferWriter();
        buffer.Write(value);
        return buffer;
    }

    private sealed class CapturingLogStorageConsumer : ILogStorageConsumer
    {
        public List<byte[]> Segments { get; } = [];

        public void Consume(LogReadBuffer buffer)
        {
            if (buffer.IsCompleted || buffer.Length == 0)
            {
                return;
            }

            var segment = new byte[buffer.Length];
            buffer.Consume(segment);
            Segments.Add(segment);
        }
    }

    private sealed class TwoByteLogStorageConsumer : ILogStorageConsumer
    {
        public List<byte[]> Segments { get; } = [];

        public void Consume(LogReadBuffer buffer)
        {
            var temp = new byte[2];
            while (buffer.TryPeek(temp))
            {
                var segment = new byte[2];
                Assert.True(buffer.TryConsume(segment));
                Segments.Add(segment);
            }

            if (buffer.IsCompleted && buffer.Length > 0)
            {
                Assert.False(buffer.TryConsume(new byte[buffer.Length + 1]));
                var segment = new byte[buffer.Length];
                buffer.Consume(segment);
                Segments.Add(segment);
            }
        }
    }

    private sealed class CompletionTrackingLogStorageConsumer : ILogStorageConsumer
    {
        public bool IsCompleted { get; private set; }

        public int CompletedLength { get; private set; }

        public void Consume(LogReadBuffer buffer)
        {
            IsCompleted = buffer.IsCompleted;
            CompletedLength = buffer.Length;
        }
    }

    private sealed class LeavingLogStorageConsumer : ILogStorageConsumer
    {
        public void Consume(LogReadBuffer buffer) { }
    }

    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= data.Length)
            {
                return new(0);
            }

            var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            ReadCount++;
            return new(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingLogEntrySink : IStateMachineResolver, IDurableStateMachine
    {
        private LogStreamId _streamId;

        public List<(LogStreamId StreamId, byte[] Payload)> Entries { get; } = [];

        object IDurableStateMachine.OperationCodec => this;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void Apply(ReadOnlySequence<byte> payload) => Entries.Add(new(_streamId, payload.ToArray()));

        public void Reset(LogStreamWriter writer) { }
        public void AppendEntries(LogStreamWriter writer) { }
        public void AppendSnapshot(LogStreamWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

}
