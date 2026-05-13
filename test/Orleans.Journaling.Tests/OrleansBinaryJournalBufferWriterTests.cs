using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class OrleansBinaryJournalBufferWriterTests
{
    private static readonly SerializerSessionPool SessionPool = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<SerializerSessionPool>();
    [Fact]
    public void Commit_WritesLegacyVarUIntFramedEntry()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(42)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 1, 2, 3 });
        entry.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal([0x09, 0x55, 1, 2, 3], bytes);
    }

    [Fact]
    public void Commit_WritesMultipleEntries()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();

        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [10]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);

        using var data = buffer.Peek();
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
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [10]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);
        using var data = buffer.GetBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(data, consumer, 1, 300);

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
    public void BinaryFormat_Replay_BuffersPreservedEntriesForRetiredStates()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [0xCC]);
        using var data = buffer.GetBuffer();
        var bufferingConsumer = new BufferingConsumer();

        ReadAll(data, bufferingConsumer, 8);

        Assert.Collection(
            bufferingConsumer.Entries,
            entry => Assert.Equal([0xAA, 0xBB], entry.Payload),
            entry => Assert.Equal([0xCC], entry.Payload));

        using var replay = new OrleansBinaryJournalBufferWriter();
        bufferingConsumer.AppendSnapshot(replay.CreateJournalStreamWriter(new JournalStreamId(8)));
        using var replayed = replay.GetBuffer();
        var activeConsumer = new CollectingConsumer();

        ReadAll(replayed, activeConsumer, 8);

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
    public void BinaryPreservedJournalEntry_StoresPayload()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0xAA, 0xBB });
        using var slice = writer.PeekSlice(writer.Length);
        var entry = new OrleansBinaryPreservedJournalEntry(slice, SessionPool);

        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, entry.FormatKey);
        Assert.Equal([0xAA, 0xBB], entry.Payload.ToArray());
    }

    [Fact]
    public void BinaryFormat_Read_HandlesSegmentedFrames()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var payload = Enumerable.Repeat((byte)0xAA, ArcBufferWriter.MinimumPageSize - 7).ToArray();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), payload);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);
        using var data = buffer.GetBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(data, consumer, 1, 300);

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
        using var buffer = new OrleansBinaryJournalBufferWriter();

        using var committed = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        committed.PayloadWriter.Write(new byte[] { 1 });
        committed.Commit();
        var committedBytes = ToArray(buffer);

        using (var aborted = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry())
        {
            aborted.PayloadWriter.Write(new byte[] { 2, 3, 4 });
        }

        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void GetBuffer_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 1 });

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var _ = buffer.GetBuffer();
        });

        Assert.Contains("active entry", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        using var committed = buffer.GetBuffer();
        Assert.Equal(0, committed.Length);
    }

    [Fact]
    public void AppendPreservedEntry_RejectsWrongFormat()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var journalStreamWriter = buffer.CreateJournalStreamWriter(new JournalStreamId(1));

        var exception = Assert.Throws<InvalidOperationException>(() => journalStreamWriter.AppendPreservedEntry(new TestPreservedJournalEntry("other", new byte[] { 1, 2, 3 })));

        Assert.Contains("cannot append preserved entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_ThrowsOnDoubleCommit()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 1 });
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
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 1 });
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
        using var buffer = new OrleansBinaryJournalBufferWriter();

        using var first = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        first.PayloadWriter.Write(new byte[] { 1 });
        first.Commit();
        buffer.Reset();

        using var second = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        second.PayloadWriter.Write(new byte[] { 2 });
        second.Commit();

        using var data = buffer.Peek();
        var reader = new SequenceReader<byte>(data.AsReadOnlySequence());
        var entry = ReadEntry(ref reader);

        Assert.Equal(2UL, entry.StreamId);
        Assert.Equal([2], entry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void GetBuffer_RemainsReadableAfterResetAndReuse()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [1, 2, 3]);
        using var committed = buffer.GetBuffer();
        var expectedCommitted = committed.ToArray();

        buffer.Reset();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(2)), [4, 5]);

        Assert.Equal(expectedCommitted, committed.ToArray());
        Assert.NotEqual(expectedCommitted, ToArray(buffer));
    }

    [Fact]
    public void Reset_ThrowsWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 1 });

        var exception = Assert.Throws<InvalidOperationException>(buffer.Reset);

        Assert.Contains("active", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        Assert.Empty(ToArray(buffer));
    }

    [Fact]
    public void Commit_WritesVariableLengthFrameAcrossSegments()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var payload = Enumerable.Repeat((byte)42, ArcBufferWriter.MinimumPageSize).ToArray();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.PayloadWriter.Write(payload);
        entry.Commit();

        using var data = buffer.Peek();
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
        using var buffer = new OrleansBinaryJournalBufferWriter();
        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(7)).BeginEntry();
        entry.PayloadWriter.Write(new byte[] { 8, 9 });
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
    [InlineData(new byte[] { 0x03, 0x00 }, "truncated varuint32 state id")]
    [InlineData(
        new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        "malformed varuint32 state id")]
    public void BinaryFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(new ArcBufferReader(data), isCompleted: true);
            var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey);
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        });

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormat_Read_ReportsMalformedFrameOffsetAfterCompleteEntries()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 0x0B, 1, 2]);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(new ArcBufferReader(data), isCompleted: true);
            var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(8));
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        });

        Assert.Contains($"byte offset {entryBytes.Length}", exception.Message, StringComparison.Ordinal);
        Assert.Single(consumer.Entries);
    }

    private static byte[] ToArray(OrleansBinaryJournalBufferWriter buffer)
    {
        using var slice = buffer.Peek();
        return slice.ToArray();
    }

    private static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.PayloadWriter.Write(payload);
        entry.Commit();
    }

    private static ArcBufferWriter CreateWriter(ReadOnlySpan<byte> bytes)
    {
        var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer;
    }

    private static void ReadAll(ArcBuffer data, IReplayConsumer consumer, params uint[] streamIds)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new JournalBufferReader(new ArcBufferReader(writer), isCompleted: true);
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(streamIds));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        Assert.Equal(0, reader.Length);
    }

    private static (uint Length, uint StreamId, ReadOnlySequence<byte> Payload) ReadEntry(ref SequenceReader<byte> reader)
    {
        var lengthReader = Reader.Create(reader.UnreadSequence, session: null!);
        var length = lengthReader.ReadVarUInt32();
        reader.Advance(lengthReader.Position);
        var entry = reader.Sequence.Slice(reader.Consumed, length);
        var streamIdReader = Reader.Create(entry, session: null!);
        var streamId = streamIdReader.ReadVarUInt32();
        var payload = entry.Slice(streamIdReader.Position);
        reader.Advance(length);

        return (length, streamId, payload);
    }

    private interface IReplayConsumer
    {
        (JournalStreamId StreamId, IJournaledState State)[] Bind(params uint[] streamIds);
    }

    private sealed class CollectingConsumer : IReplayConsumer
    {

        public List<(uint StreamId, byte[] Payload)> Entries { get; } = [];

        public (JournalStreamId StreamId, IJournaledState State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IJournaledState State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamConsumer(this, streamId));
            }

            return bindings;
        }

        private sealed class StreamConsumer(CollectingConsumer owner, JournalStreamId streamId) : IJournaledState
        {
            void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
                owner.Entries.Add((streamId.Value, entry.Reader.ToArray()));

            public void Reset(JournalStreamWriter writer) { }
            public void AppendEntries(JournalStreamWriter writer) { }
            public void AppendSnapshot(JournalStreamWriter writer) { }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

    private sealed class BufferingConsumer : IReplayConsumer
    {
        private readonly List<IPreservedJournalEntry> _preservedEntries = [];

        public List<(uint StreamId, byte[] Payload)> Entries { get; } = [];

        public IReadOnlyList<IPreservedJournalEntry> PreservedEntries => _preservedEntries;

        public (JournalStreamId StreamId, IJournaledState State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IJournaledState State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamConsumer(this, streamId));
            }

            return bindings;
        }

        public void AppendSnapshot(JournalStreamWriter writer)
        {
            foreach (var entry in _preservedEntries)
            {
                writer.AppendPreservedEntry(entry);
            }
        }

        private sealed class StreamConsumer(BufferingConsumer owner, JournalStreamId streamId) : IJournaledState
        {
            void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context)
            {
                var preservedEntry = new TestPreservedJournalEntry(entry.FormatKey, entry.Reader.ToArray());
                owner._preservedEntries.Add(preservedEntry);
                owner.Entries.Add((streamId.Value, preservedEntry.Payload.ToArray()));
            }

            public void Reset(JournalStreamWriter writer) => owner._preservedEntries.Clear();
            public void AppendEntries(JournalStreamWriter writer) { }
            public void AppendSnapshot(JournalStreamWriter writer) { }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

    private sealed class TestPreservedJournalEntry : IPreservedJournalEntry
    {
        public TestPreservedJournalEntry()
            : this(OrleansBinaryJournalFormat.JournalFormatKey, new byte[] { 1, 2, 3 })
        {
        }

        public TestPreservedJournalEntry(string formatKey, ReadOnlyMemory<byte> payload)
        {
            FormatKey = formatKey;
            Payload = payload.ToArray();
        }

        public ReadOnlyMemory<byte> Payload { get; }

        public string FormatKey { get; }
    }
}
