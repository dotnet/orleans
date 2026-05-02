using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class OrleansBinaryLogBatchWriterTests
{
    [Fact]
    public void Commit_WritesFixed32FramedEntry()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();

        using var entry = buffer.CreateLogStreamWriter(new LogStreamId(42)).BeginEntry();
        entry.Writer.Write([1, 2, 3]);
        entry.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(42, bytes[4]);
        Assert.Equal([1, 2, 3], bytes[5..]);
    }

    [Fact]
    public void Commit_WritesMultipleEntries()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();

        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(1)), [10]);
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(300)), [20, 21]);

        var reader = new SequenceReader<byte>(buffer.AsReadOnlySequence());
        var firstEntry = ReadEntry(ref reader);
        var secondEntry = ReadEntry(ref reader);

        Assert.Equal(2U, firstEntry.Length);
        Assert.Equal(1UL, firstEntry.StreamId);
        Assert.Equal([10], firstEntry.Payload.ToArray());
        Assert.Equal(4U, secondEntry.Length);
        Assert.Equal(300UL, secondEntry.StreamId);
        Assert.Equal([20, 21], secondEntry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void BinaryFormat_Read_ParsesConcatenatedEntries()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(1)), [10]);
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(300)), [20, 21]);
        using var data = buffer.GetCommittedBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(data, consumer);

        Assert.Collection(
            consumer.Entries,
            entry =>
            {
                Assert.Equal(1UL, entry.StreamId);
                Assert.Equal([10], entry.Payload);
            },
            entry =>
            {
                Assert.Equal(300UL, entry.StreamId);
                Assert.Equal([20, 21], entry.Payload);
            });
    }

    [Fact]
    public void BinaryFormat_Read_HandlesSegmentedFrames()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        var payload = Enumerable.Repeat((byte)0xAA, ArcBufferWriter.MinimumPageSize - 7).ToArray();
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(1)), payload);
        AppendEntry(buffer.CreateLogStreamWriter(new LogStreamId(300)), [20, 21]);
        using var data = buffer.GetCommittedBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(data, consumer);

        Assert.Collection(
            consumer.Entries,
            entry =>
            {
                Assert.Equal(1UL, entry.StreamId);
                Assert.Equal(payload, entry.Payload);
            },
            entry =>
            {
                Assert.Equal(300UL, entry.StreamId);
                Assert.Equal([20, 21], entry.Payload);
            });
    }

    [Fact]
    public void DisposeWithoutCommit_TruncatesPendingEntry()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();

        using var committed = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        committed.Writer.Write([1]);
        committed.Commit();
        var committedBytes = ToArray(buffer);

        using (var aborted = buffer.CreateLogStreamWriter(new LogStreamId(2)).BeginEntry())
        {
            aborted.Writer.Write([2, 3, 4]);
        }

        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void GetCommittedBuffer_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        var entry = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        entry.Writer.Write([1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var _ = buffer.GetCommittedBuffer();
        });

        Assert.Contains("active entry", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        using var committed = buffer.GetCommittedBuffer();
        Assert.Equal(0, committed.Length);
    }

    [Fact]
    public void AppendFormattedEntry_RejectsWrongFormattedEntryType()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        var logStreamWriter = buffer.CreateLogStreamWriter(new LogStreamId(1));

        var exception = Assert.Throws<InvalidOperationException>(() => logStreamWriter.AppendFormattedEntry(new TestFormattedLogEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_ThrowsOnDoubleCommit()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        using var entry = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        entry.Writer.Write([1]);
        entry.Commit();
        var committedBytes = ToArray(buffer);

        InvalidOperationException? exception = null;
        try
        {
            entry.Commit();
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Contains("already completed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void CommitAfterDispose_ThrowsAndKeepsEntryAborted()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        var entry = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        entry.Writer.Write([1]);
        entry.Dispose();

        InvalidOperationException? exception = null;
        try
        {
            entry.Commit();
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Contains("already completed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(ToArray(buffer));
    }

    [Fact]
    public void Reset_ReusesBuffer()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();

        using var first = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        first.Writer.Write([1]);
        first.Commit();
        buffer.Reset();

        using var second = buffer.CreateLogStreamWriter(new LogStreamId(2)).BeginEntry();
        second.Writer.Write([2]);
        second.Commit();

        var reader = new SequenceReader<byte>(buffer.AsReadOnlySequence());
        var entry = ReadEntry(ref reader);

        Assert.Equal(2UL, entry.StreamId);
        Assert.Equal([2], entry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void Reset_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        var entry = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        entry.Writer.Write([1]);

        var exception = Assert.Throws<InvalidOperationException>(buffer.Reset);

        Assert.Contains("active", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        Assert.Empty(ToArray(buffer));
    }

    [Fact]
    public void Commit_BackpatchesLengthAcrossSegments()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        buffer.Write(new byte[ArcBufferWriter.MinimumPageSize - 2]);

        using var entry = buffer.CreateLogStreamWriter(new LogStreamId(1)).BeginEntry();
        entry.Writer.Write([42]);
        entry.Commit();

        var bytes = ToArray(buffer);
        var offset = ArcBufferWriter.MinimumPageSize - 2;

        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
        Assert.Equal(1, bytes[offset + 4]);
        Assert.Equal(42, bytes[offset + 5]);
    }

    [Fact]
    public void ReadOnlyStream_ReadsCommittedBytes()
    {
        using var buffer = new OrleansBinaryLogBatchWriter();
        using var entry = buffer.CreateLogStreamWriter(new LogStreamId(7)).BeginEntry();
        entry.Writer.Write([8, 9]);
        entry.Commit();
        var expected = ToArray(buffer);

        using var stream = buffer.AsReadOnlyStream();
        var actual = new byte[expected.Length];
        Assert.Equal(expected.Length, stream.Read(actual));
        Assert.Equal(expected, actual);

        stream.Position = 4;
        Assert.Equal(7, stream.ReadByte());
    }

    [Fact]
    public void LogEntryReader_ReadsValuesAndRemainingPayload()
    {
        var payload = new byte[] { 0xAC, 0x02, 0x03, 0xAA, 0xBB, 0xCC };
        var reader = new LogEntryReader(new ReadOnlySequence<byte>(payload));

        Assert.Equal(300U, reader.ReadVarUInt32());
        var bytes = reader.ReadBytes(2);

        Assert.Equal([0x03, 0xAA], bytes.ToArray());
        Assert.Equal([0xBB, 0xCC], reader.Remaining.ToArray());
        Assert.False(reader.End);
    }

    [Fact]
    public void LogEntryReader_ThrowsOnTruncatedPayload()
    {
        var reader = new LogEntryReader(new ReadOnlySequence<byte>(new byte[] { 1, 2 }));

        var thrown = false;
        try
        {
            reader.ReadBytes(3);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        Assert.True(thrown);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 }, "truncated fixed32 entry length prefix")]
    [InlineData(new byte[] { 0, 0, 0, 0 }, "zero-length entries")]
    [InlineData(new byte[] { 5, 0, 0, 0, 1, 2 }, "exceeds remaining input bytes")]
    [InlineData(new byte[] { 1, 0, 0, 0, 0x80 }, "truncated varuint64 state-machine id")]
    [InlineData(new byte[] { 1, 0, 0, 0, 1 }, "missing operation payload")]
    [InlineData(
        new byte[] { 11, 0, 0, 0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0 },
        "malformed varuint64 state-machine id")]
    [InlineData(new byte[] { 255, 255, 255, 255 }, "exceeds remaining input bytes")]
    public void BinaryFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var reader = new ArcBufferReader(data);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: true));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    private static byte[] ToArray(OrleansBinaryLogBatchWriter buffer)
    {
        using var slice = buffer.PeekSlice();
        return slice.ToArray();
    }

    private static void AppendEntry(LogStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private static ArcBufferWriter CreateWriter(ReadOnlySpan<byte> bytes)
    {
        var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer;
    }

    private static void ReadAll(ArcBuffer data, IStateMachineResolver consumer)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new ArcBufferReader(writer);
        while (((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: true))
        {
        }
    }

    private static (uint Length, ulong StreamId, ReadOnlySequence<byte> Payload) ReadEntry(ref SequenceReader<byte> reader)
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        Assert.True(reader.TryCopyTo(lengthBytes));
        reader.Advance(sizeof(uint));

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        var entry = reader.Sequence.Slice(reader.Consumed, length);
        var entryReader = new SequenceReader<byte>(entry);
        var streamId = VarIntHelper.ReadVarUInt64(ref entryReader);
        var payload = entry.Slice(entryReader.Consumed);
        reader.Advance(length);

        return (length, streamId, payload);
    }

    private sealed class CollectingConsumer : IStateMachineResolver, IDurableStateMachine
    {
        private LogStreamId _streamId;

        public List<(ulong StreamId, byte[] Payload)> Entries { get; } = [];

        object IDurableStateMachine.OperationCodec => this;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void Apply(ReadOnlySequence<byte> payload) => Entries.Add((_streamId.Value, payload.ToArray()));

        public void Reset(LogStreamWriter writer) { }
        public void AppendEntries(LogStreamWriter writer) { }
        public void AppendSnapshot(LogStreamWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class TestFormattedLogEntry : IFormattedLogEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = new byte[] { 1, 2, 3 };
    }
}
