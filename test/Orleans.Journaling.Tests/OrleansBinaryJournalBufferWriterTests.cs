using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class OrleansBinaryJournalBufferWriterTests
{
    private static readonly SerializerSessionPool SessionPool = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<SerializerSessionPool>();
    [Fact]
    public void Commit_WritesFixedWidthFramedEntry()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(42)).BeginEntry();
        entry.Writer.Write(new byte[] { 1, 2, 3 });
        entry.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal([0, 7, 0, 0, 0, 42, 0, 0, 0, 1, 2, 3], bytes);
    }

    [Theory]
    [InlineData(new byte[] { }, 0U, 0, false)]
    [InlineData(new byte[] { 0 }, 0U, 0, false)]
    [InlineData(new byte[] { 0, 7, 0, 0 }, 0U, 0, false)]
    [InlineData(new byte[] { 0, 7, 0, 0, 0 }, 7U, 5, true)]
    public void TryReadVersionAndLength_ReturnsFalseUntilPrefixIsComplete(
        byte[] bytes,
        uint expectedLength,
        int expectedPrefixLength,
        bool expectedResult)
    {
        using var writer = CreateWriter(bytes);
        using var buffer = writer.PeekSlice(writer.Length);
        var result = OrleansBinaryJournalReader.TryReadVersionAndLength(
            buffer,
            out var version,
            out var length,
            out var prefixLength);

        Assert.Equal(expectedResult, result);
        Assert.Equal(0, version);
        Assert.Equal(expectedLength, length);
        Assert.Equal(expectedPrefixLength, prefixLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void TryReadVersionAndLength_RejectsUnsupportedFramingVersion(byte version)
    {
        using var writer = CreateWriter([version, 4, 0, 0, 0, 1, 0, 0, 0]);
        using var buffer = writer.PeekSlice(writer.Length);
        Assert.Throws<NotSupportedException>(() =>
            OrleansBinaryJournalReader.TryReadVersionAndLength(buffer, out _, out _, out _));
    }

    [Fact]
    public void Commit_WritesMultipleEntries()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();

        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [10]);
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(300)), [20, 21]);

        using var data = buffer.GetBuffer();
        var offset = 0;
        var firstEntry = ReadEntry(data, ref offset);
        var secondEntry = ReadEntry(data, ref offset);

        Assert.Equal(5U, firstEntry.Length);
        Assert.Equal(1UL, firstEntry.StreamId);
        Assert.Equal([10], firstEntry.Payload);
        Assert.Equal(6U, secondEntry.Length);
        Assert.Equal(300UL, secondEntry.StreamId);
        Assert.Equal([20, 21], secondEntry.Payload);
        Assert.Equal(data.Length, offset);
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
        bufferingConsumer.WriteSnapshot(replay.CreateJournalStreamWriter(new JournalStreamId(8)));
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
        committed.Writer.Write(new byte[] { 1 });
        committed.Commit();
        var committedBytes = ToArray(buffer);

        using (var aborted = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry())
        {
            aborted.Writer.Write(new byte[] { 2, 3, 4 });
        }

        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void GetBuffer_ReturnsCommittedBytesWhenEntryIsActive()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(1)), [1]);
        var committedBytes = ToArray(buffer);
        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        entry.Writer.Write(new byte[] { 2 });

        using var committed = buffer.GetBuffer();

        Assert.Equal(committedBytes, committed.ToArray());
        entry.Commit();
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
        entry.Writer.Write(new byte[] { 1 });
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
        entry.Writer.Write(new byte[] { 1 });
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
        first.Writer.Write(new byte[] { 1 });
        first.Commit();
        buffer.Reset();

        using var second = buffer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        second.Writer.Write(new byte[] { 2 });
        second.Commit();

        using var data = buffer.GetBuffer();
        var offset = 0;
        var entry = ReadEntry(data, ref offset);

        Assert.Equal(2UL, entry.StreamId);
        Assert.Equal([2], entry.Payload);
        Assert.Equal(data.Length, offset);
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
        entry.Writer.Write(new byte[] { 1 });

        var exception = Assert.Throws<InvalidOperationException>(buffer.Reset);

        Assert.Contains("active", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
        Assert.Empty(ToArray(buffer));
    }

    [Fact]
    public void Commit_WritesFixedWidthFrameAcrossSegments()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        var payload = Enumerable.Repeat((byte)42, ArcBufferWriter.MinimumPageSize).ToArray();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();

        using var data = buffer.GetBuffer();
        var offset = 0;
        var written = ReadEntry(data, ref offset);

        Assert.Equal((uint)payload.Length + sizeof(uint), written.Length);
        Assert.Equal(1UL, written.StreamId);
        Assert.Equal(payload, written.Payload);
        Assert.Equal(data.Length, offset);
    }

    [Theory]
    [InlineData(new byte[] { 0 }, "truncated fixed-width entry header")]
    [InlineData(new byte[] { 0, 0, 0, 0 }, "truncated fixed-width entry header")]
    [InlineData(new byte[] { 0, 0, 0, 0, 0 }, "smaller than the fixed-width state id")]
    [InlineData(new byte[] { 0, 3, 0, 0, 0, 1, 0, 0 }, "smaller than the fixed-width state id")]
    [InlineData(new byte[] { 0, 8, 0, 0, 0, 1, 0, 0, 0, 0xAA }, "exceeds remaining input bytes")]
    public void BinaryFormat_Read_RejectsMalformedFramesWithoutConsumingInput(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var reader = new JournalBufferReader(data.Reader, isCompleted: true);
        var consumer = new CollectingConsumer();
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(1));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
        Assert.Equal(bytes.Length, reader.Length);
    }

    [Fact]
    public void BinaryFormat_Read_ReportsMalformedFrameOffsetAfterCompleteEntries()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();
        AppendEntry(buffer.CreateJournalStreamWriter(new JournalStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 0, 8, 0, 0, 0, 1, 0, 0, 0]);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(data.Reader, isCompleted: true);
            var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(8));
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        });

        Assert.Contains($"byte offset {entryBytes.Length}", exception.Message, StringComparison.Ordinal);
        Assert.Single(consumer.Entries);
    }

    private static byte[] ToArray(OrleansBinaryJournalBufferWriter buffer)
    {
        using var slice = buffer.GetBuffer();
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

    private static void ReadAll(ArcBuffer data, IReplayConsumer consumer, params uint[] streamIds)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new JournalBufferReader(writer.Reader, isCompleted: true);
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(streamIds));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        Assert.Equal(0, reader.Length);
    }

    private static (uint Length, uint StreamId, byte[] Payload) ReadEntry(ArcBuffer input, ref int offset)
    {
        var remaining = input.UnsafeSlice(offset, input.Length - offset);
        if (!OrleansBinaryJournalReader.TryReadVersionAndLength(remaining, out _, out var length, out var lengthPrefixLength))
        {
            throw new InvalidOperationException("The binary journal entry stream is malformed.");
        }

        var entryStart = offset + lengthPrefixLength;
        if (length == 0 || length > input.Length - entryStart)
        {
            throw new InvalidOperationException("The binary journal entry stream is malformed.");
        }

        var entry = input.UnsafeSlice(entryStart, checked((int)length));
        var streamId = OrleansBinaryJournalReader.ReadUInt32LittleEndian(entry.UnsafeSlice(0, sizeof(uint)));
        var payload = entry.UnsafeSlice(sizeof(uint), entry.Length - sizeof(uint)).ToArray();
        offset = checked(entryStart + (int)length);
        return (length, streamId, payload);
    }

    private interface IReplayConsumer
    {
        (JournalStreamId StreamId, IStateMachine State)[] Bind(params uint[] streamIds);
    }

    private sealed class CollectingConsumer : IReplayConsumer
    {

        public List<(uint StreamId, byte[] Payload)> Entries { get; } = [];

        public (JournalStreamId StreamId, IStateMachine State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IStateMachine State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamConsumer(this, streamId));
            }

            return bindings;
        }

        private sealed class StreamConsumer(CollectingConsumer owner, JournalStreamId streamId) : IStateMachine
        {
            void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
                owner.Entries.Add((streamId.Value, entry.Reader.ToArray()));

            public void Reset(JournalStreamWriter writer) { }
            public void WritePendingEntries(JournalStreamWriter writer) { }
            public void WriteSnapshot(JournalStreamWriter writer) { }
        }
    }

    private sealed class BufferingConsumer : IReplayConsumer
    {
        private readonly List<IPreservedJournalEntry> _preservedEntries = [];

        public List<(uint StreamId, byte[] Payload)> Entries { get; } = [];

        public IReadOnlyList<IPreservedJournalEntry> PreservedEntries => _preservedEntries;

        public (JournalStreamId StreamId, IStateMachine State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IStateMachine State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamConsumer(this, streamId));
            }

            return bindings;
        }

        public void WriteSnapshot(JournalStreamWriter writer)
        {
            foreach (var entry in _preservedEntries)
            {
                writer.AppendPreservedEntry(entry);
            }
        }

        private sealed class StreamConsumer(BufferingConsumer owner, JournalStreamId streamId) : IStateMachine
        {
            void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context)
            {
                var preservedEntry = new TestPreservedJournalEntry(entry.FormatKey, entry.Reader.ToArray());
                owner._preservedEntries.Add(preservedEntry);
                owner.Entries.Add((streamId.Value, preservedEntry.Payload.ToArray()));
            }

            public void Reset(JournalStreamWriter writer) => owner._preservedEntries.Clear();
            public void WritePendingEntries(JournalStreamWriter writer) { }
            public void WriteSnapshot(JournalStreamWriter writer) { }
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void BinaryFormat_Replay_RejectsUnsupportedFramingVersionWithoutConsumingInput(byte version)
    {
        using var data = CreateWriter([version, 5, 0, 0, 0, 1, 0, 0, 0, 0xAA]);
        var reader = new JournalBufferReader(data.Reader, isCompleted: true);
        var originalLength = reader.Length;
        var consumer = new CollectingConsumer();
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(1));

        var exception = Assert.Throws<NotSupportedException>(() =>
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context));

        Assert.Contains($"Unsupported binary journal entry format version at byte offset 0: Unsupported framing version: {version}.", exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
        Assert.Equal(originalLength, reader.Length);
    }

    [Fact]
    public void BinaryFormat_Replay_BuffersIncompleteFrameUntilComplete()
    {
        using var batch = new OrleansBinaryJournalBufferWriter();
        AppendEntry(batch.CreateJournalStreamWriter(new JournalStreamId(1)), [10, 20, 30]);
        var bytes = ToArray(batch);
        using var data = new ArcBufferWriter();
        var reader = new JournalBufferReader(data.Reader, isCompleted: false);
        var consumer = new CollectingConsumer();
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(1));
        IJournalFormat format = new OrleansBinaryJournalFormat(SessionPool);

        for (var i = 0; i < bytes.Length - 1; i++)
        {
            data.Write(bytes.AsSpan(i, 1));
            format.Replay(reader, context);
            Assert.Empty(consumer.Entries);
            Assert.Equal(i + 1, reader.Length);
        }

        data.Write(bytes.AsSpan(bytes.Length - 1));
        format.Replay(new JournalBufferReader(data.Reader, isCompleted: true), context);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal(1U, entry.StreamId);
        Assert.Equal([10, 20, 30], entry.Payload);
        Assert.Equal(0, reader.Length);
    }
}
