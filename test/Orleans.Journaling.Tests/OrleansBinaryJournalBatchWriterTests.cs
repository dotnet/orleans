using System.Buffers;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class OrleansBinaryJournalBatchWriterTests
{
    [Fact]
    public void Commit_WritesLegacyVarUIntFramedEntry()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(42)).BeginEntry();
        entry.Writer.Write([1, 2, 3]);
        entry.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal([0x09, 0x55, 1, 2, 3], bytes);
    }

    [Fact]
    public void Commit_WritesMultipleEntries()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();

        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [10]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);

        using var data = buffer.PeekSlice();
        var reader = new SequenceReader<byte>(data.AsReadOnlySequence());
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
        using var buffer = new OrleansBinaryJournalBatchWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [10]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);
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
    public void BinaryFormat_Read_BuffersFormattedEntriesForRetiredStates()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [0xCC]);
        using var data = buffer.GetCommittedBuffer();
        var bufferingConsumer = new BufferingConsumer();

        ReadAll(data, bufferingConsumer);

        Assert.Collection(
            bufferingConsumer.Entries,
            entry => Assert.Equal([0xAA, 0xBB], entry.Payload),
            entry => Assert.Equal([0xCC], entry.Payload));

        using var replay = new OrleansBinaryJournalBatchWriter();
        bufferingConsumer.AppendSnapshot(replay.CreateJournalStreamWriter(new JournalStreamId(8)));
        using var replayed = replay.GetCommittedBuffer();
        var activeConsumer = new CollectingConsumer();

        ReadAll(replayed, activeConsumer);

        Assert.Collection(
            activeConsumer.Entries,
            entry =>
            {
                Assert.Equal(8UL, entry.StreamId);
                Assert.Equal([0xAA, 0xBB], entry.Payload);
            },
            entry =>
            {
                Assert.Equal(8UL, entry.StreamId);
                Assert.Equal([0xCC], entry.Payload);
            });
    }

    [Fact]
    public void BinaryFormattedJournalEntry_Apply_UsesOperationCodec()
    {
        var entry = new OrleansBinaryFormattedJournalEntry(new ReadOnlySequence<byte>(new byte[] { 0xAA, 0xBB }));
        var consumer = new CollectingConsumer();

        entry.Apply(consumer);

        var applied = Assert.Single(consumer.Entries);
        Assert.Equal(0UL, applied.StreamId);
        Assert.Equal([0xAA, 0xBB], applied.Payload);
    }

    [Fact]
    public void BinaryFormat_Read_HandlesSegmentedFrames()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var payload = Enumerable.Repeat((byte)0xAA, ArcBufferWriter.MinimumPageSize - 7).ToArray();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), payload);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);
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
        using var buffer = new OrleansBinaryJournalBatchWriter();

        using var committed = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        committed.Writer.Write([1]);
        committed.Commit();
        var committedBytes = ToArray(buffer);

        using (var aborted = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry())
        {
            aborted.Writer.Write([2, 3, 4]);
        }

        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void GetCommittedBuffer_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
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
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var journalStreamWriter = buffer.CreateJournalStreamWriter(new JournalStreamId(1));

        var exception = Assert.Throws<InvalidOperationException>(() => journalStreamWriter.AppendFormattedEntry(new TestFormattedJournalEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_ThrowsOnDoubleCommit()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
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
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
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
        using var buffer = new OrleansBinaryJournalBatchWriter();

        using var first = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        first.Writer.Write([1]);
        first.Commit();
        buffer.Reset();

        using var second = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        second.Writer.Write([2]);
        second.Commit();

        using var data = buffer.PeekSlice();
        var reader = new SequenceReader<byte>(data.AsReadOnlySequence());
        var entry = ReadEntry(ref reader);

        Assert.Equal(2UL, entry.StreamId);
        Assert.Equal([2], entry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void Reset_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.Writer.Write([1]);

        var exception = Assert.Throws<InvalidOperationException>(buffer.Reset);

        Assert.Contains("active", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        Assert.Empty(ToArray(buffer));
    }

    [Fact]
    public void Commit_WritesVariableLengthFrameAcrossSegments()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        var payload = Enumerable.Repeat((byte)42, ArcBufferWriter.MinimumPageSize).ToArray();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();

        using var data = buffer.PeekSlice();
        var reader = new SequenceReader<byte>(data.AsReadOnlySequence());
        var written = ReadEntry(ref reader);

        Assert.Equal((uint)payload.Length + 1, written.Length);
        Assert.Equal(1UL, written.StreamId);
        Assert.Equal(payload, written.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void ReadOnlyStream_ReadsCommittedBytes()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(7)).BeginEntry();
        entry.Writer.Write([8, 9]);
        entry.Commit();
        var expected = ToArray(buffer);

        using var stream = buffer.AsReadOnlyStream();
        var actual = new byte[expected.Length];
        Assert.Equal(expected.Length, stream.Read(actual));
        Assert.Equal(expected, actual);

        stream.Position = 1;
        Assert.Equal(0x0F, stream.ReadByte());
    }

    [Theory]
    [InlineData(new byte[] { 0x02 }, "truncated varuint32 entry length prefix")]
    [InlineData(new byte[] { 0x01 }, "zero-length entries")]
    [InlineData(new byte[] { 0x0B, 1, 2 }, "exceeds remaining input bytes")]
    [InlineData(new byte[] { 0x03, 0x00 }, "truncated varuint64 state id")]
    [InlineData(
        new byte[] { 0x05, 0x00, 0x00 },
        "malformed varuint64 state id")]
    public void BinaryFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var reader = new JournalReadBuffer(new ArcBufferReader(data), isCompleted: true);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((IJournalFormat)OrleansBinaryJournalFormat.Instance).Read(reader, consumer));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormat_Read_ReportsMalformedFrameOffsetAfterCompleteEntries()
    {
        using var buffer = new OrleansBinaryJournalBatchWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetCommittedBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 0x0B, 1, 2]);
        var reader = new JournalReadBuffer(new ArcBufferReader(data), isCompleted: true);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((IJournalFormat)OrleansBinaryJournalFormat.Instance).Read(reader, consumer));

        Assert.Contains($"byte offset {entryBytes.Length}", exception.Message, StringComparison.Ordinal);
        Assert.Single(consumer.Entries);
    }

    private static byte[] ToArray(OrleansBinaryJournalBatchWriter buffer)
    {
        using var slice = buffer.PeekSlice();
        return slice.ToArray();
    }

    private static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
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

    private static void ReadAll(ArcBuffer data, IStateResolver consumer)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new JournalReadBuffer(new ArcBufferReader(writer), isCompleted: true);
        ((IJournalFormat)OrleansBinaryJournalFormat.Instance).Read(reader, consumer);
        Assert.Equal(0, reader.Length);
    }

    private static (uint Length, ulong StreamId, ReadOnlySequence<byte> Payload) ReadEntry(ref SequenceReader<byte> reader)
    {
        var lengthReader = Reader.Create(reader.UnreadSequence, session: null!);
        var length = lengthReader.ReadVarUInt32();
        reader.Advance(lengthReader.Position);
        var entry = reader.Sequence.Slice(reader.Consumed, length);
        var streamIdReader = Reader.Create(entry, session: null!);
        var streamId = streamIdReader.ReadVarUInt64();
        var payload = entry.Slice(streamIdReader.Position);
        reader.Advance(length);

        return (length, streamId, payload);
    }

    private sealed class CollectingConsumer : IStateResolver, IJournaledState, IOrleansBinaryJournalEntryCodec
    {
        private JournalStreamId _streamId;

        public List<(ulong StreamId, byte[] Payload)> Entries { get; } = [];

        object IJournaledState.OperationCodec => this;

        public IJournaledState ResolveState(JournalStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        void IOrleansBinaryJournalEntryCodec.Apply(ReadOnlySequence<byte> payload, IJournaledState state) => Entries.Add((_streamId.Value, payload.ToArray()));

        public void Reset(JournalStreamWriter writer) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class BufferingConsumer : IStateResolver, IJournaledState, IFormattedJournalEntryBuffer
    {
        private readonly List<IFormattedJournalEntry> _formattedEntries = [];
        private JournalStreamId _streamId;

        public List<(ulong StreamId, byte[] Payload)> Entries { get; } = [];

        public IReadOnlyList<IFormattedJournalEntry> FormattedEntries => _formattedEntries;

        object IJournaledState.OperationCodec => this;

        public IJournaledState ResolveState(JournalStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void AddFormattedEntry(IFormattedJournalEntry entry)
        {
            _formattedEntries.Add(entry);
            Entries.Add((_streamId.Value, entry.Payload.ToArray()));
        }

        public void Reset(JournalStreamWriter writer) => _formattedEntries.Clear();
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer)
        {
            foreach (var entry in _formattedEntries)
            {
                writer.AppendFormattedEntry(entry);
            }
        }

        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class TestFormattedJournalEntry : IFormattedJournalEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = new byte[] { 1, 2, 3 };

        public void Apply(IJournaledState state) => throw new NotSupportedException();
    }
}
