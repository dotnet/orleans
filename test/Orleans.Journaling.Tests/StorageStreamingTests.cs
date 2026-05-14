using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class StorageStreamingTests
{
    private static readonly SerializerSessionPool SessionPool = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<SerializerSessionPool>();
    [Fact]
    public async Task VolatileStorage_AppendAndReplaceStoreRawBuffers()
    {
        var storage = new VolatileJournalStorage();
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
        var storage = new VolatileJournalStorage();
        var rawBytes = new byte[] { 255, 0, 1 };
        var consumer = new CapturingJournalStorageConsumer();

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
        var storage = new VolatileJournalStorage();
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5 };
        var consumer = new CapturingJournalStorageConsumer();

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
        var consumer = new TwoByteJournalStorageConsumer();

        var totalBytesRead = await consumer.ReadAsync(stream, metadata: null, CancellationToken.None);

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
        var consumer = new TwoByteJournalStorageConsumer();
        ReadOnlyMemory<byte>[] segments = [new byte[] { 1 }, new byte[] { 2, 3 }, new byte[] { 4 }];

        consumer.Read(segments, metadata: null, complete: true);

        Assert.Collection(
            consumer.Segments,
            segment => Assert.Equal([1, 2], segment),
            segment => Assert.Equal([3, 4], segment));
    }

    [Fact]
    public void MemoryConsumerHelper_CompletesEmptyInput()
    {
        var consumer = new CompletionTrackingJournalStorageConsumer();

        consumer.Read(ReadOnlyMemory<byte>.Empty, metadata: null, complete: true);

        Assert.True(consumer.IsCompleted);
        Assert.Equal(0, consumer.CompletedLength);
    }

    [Fact]
    public void MemoryConsumerHelper_RejectsUnconsumedDataWhenNotCompleted()
    {
        var consumer = new LeavingJournalStorageConsumer();

        Assert.Throws<InvalidOperationException>(() => consumer.Read(new byte[] { 1 }, metadata: null, complete: false));
    }

    [Fact]
    public void MemoryConsumerHelper_RejectsUnconsumedDataWhenCompleted()
    {
        var consumer = new LeavingJournalStorageConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() => consumer.Read(new byte[] { 1 }, metadata: null, complete: true));

        Assert.Contains("did not read all supplied journal data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamConsumerHelper_RejectsUnconsumedDataWhenCompleted()
    {
        var stream = new ChunkedReadStream([1], chunkSize: 1);
        var consumer = new LeavingJournalStorageConsumer();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.ReadAsync(stream, metadata: null, CancellationToken.None));

        Assert.Contains("did not read all supplied journal data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryFormatRead_RejectsTruncatedVarUIntFrame()
    {
        using var writer = CreateWriter([0x15, 1, 2]);
        var consumer = new CapturingJournalEntrySink();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(writer.Reader, isCompleted: true);
            var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(8));
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        });

        Assert.Contains("exceeds remaining input bytes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_WaitsForIncompleteFrameWhenInputIsNotCompleted()
    {
        using var writer = CreateWriter([0x15, 1, 2]);
        var reader = new JournalBufferReader(writer.Reader, isCompleted: false);
        var consumer = new CapturingJournalEntrySink();

        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(8));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);

        Assert.Equal(3, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_ConsumesCompletePrefixAndWaitsForPartialSuffix()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 10, 0]);
        var reader = new JournalBufferReader(data.Reader, isCompleted: false);
        var consumer = new CapturingJournalEntrySink();

        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(8));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((uint)8, entry.StreamId.Value);
        Assert.Equal([1, 2, 3], entry.Payload);
        Assert.Equal(2, reader.Length);
    }

    private static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
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

    private sealed class CapturingJournalStorageConsumer : IJournalStorageConsumer
    {
        public List<byte[]> Segments { get; } = [];

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata)
        {
            if (buffer.IsCompleted || buffer.Length == 0)
            {
                return;
            }

            var segment = new byte[buffer.Length];
            buffer.Read(segment);
            Segments.Add(segment);
        }
    }

    private sealed class TwoByteJournalStorageConsumer : IJournalStorageConsumer
    {
        public List<byte[]> Segments { get; } = [];

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata)
        {
            var temp = new byte[2];
            while (buffer.TryPeek(temp))
            {
                var segment = new byte[2];
                Assert.True(buffer.TryRead(segment));
                Segments.Add(segment);
            }

            if (buffer.IsCompleted && buffer.Length > 0)
            {
                Assert.False(buffer.TryRead(new byte[buffer.Length + 1]));
                var segment = new byte[buffer.Length];
                buffer.Read(segment);
                Segments.Add(segment);
            }
        }
    }

    private sealed class CompletionTrackingJournalStorageConsumer : IJournalStorageConsumer
    {
        public bool IsCompleted { get; private set; }

        public int CompletedLength { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata)
        {
            IsCompleted = buffer.IsCompleted;
            CompletedLength = buffer.Length;
        }
    }

    private sealed class LeavingJournalStorageConsumer : IJournalStorageConsumer
    {
        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata) { }
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

    private sealed class CapturingJournalEntrySink
    {
        public List<(JournalStreamId StreamId, byte[] Payload)> Entries { get; } = [];

        public (JournalStreamId StreamId, IJournaledState State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IJournaledState State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamSink(this, streamId));
            }

            return bindings;
        }

        private sealed class StreamSink(CapturingJournalEntrySink owner, JournalStreamId streamId) : IJournaledState
        {
            void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
                owner.Entries.Add(new(streamId, entry.Reader.ToArray()));

            public void Reset(JournalStreamWriter writer) { }
            public void AppendEntries(JournalStreamWriter writer) { }
            public void AppendSnapshot(JournalStreamWriter writer) { }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

}
