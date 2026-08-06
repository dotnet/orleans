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
    public void Commit_WritesV1FixedWidthFramedEntry()
    {
        using var buffer = new OrleansBinaryJournalBufferWriter();

        using var entry = buffer.CreateJournalStreamWriter(new JournalStreamId(42)).BeginEntry();
        entry.Writer.Write(new byte[] { 1, 2, 3 });
        entry.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal([1, 7, 0, 0, 0, 42, 0, 0, 0, 1, 2, 3], bytes);
    }

    [Fact]
    public void BinaryFormat_Read_ParsesLegacyVarUIntFramedEntry()
    {
        using var buffer = CreateLegacyWriter(new JournalStreamId(42), [0, 1, 2, 3]);
        using var data = buffer.PeekSlice(buffer.Length);
        var consumer = new CollectingConsumer();

        ReadAll(data, consumer, 42);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal(42U, entry.StreamId);
        Assert.Equal([1, 2, 3], entry.Payload);
    }

    [Theory]
    [InlineData(new byte[] { }, 0, 0U, 0, false)]
    [InlineData(new byte[] { 1 }, 1, 0U, 0, false)]
    [InlineData(new byte[] { 1, 7, 0, 0 }, 1, 0U, 0, false)]
    [InlineData(new byte[] { 1, 7, 0, 0, 0 }, 1, 7U, 5, true)]
    [InlineData(new byte[] { 0x02 }, 0, 0U, 0, false)]
    [InlineData(new byte[] { 0x17 }, 0, 11U, 1, true)]
    public void TryReadVersionAndLength_ReturnsFalseUntilPrefixIsComplete(
        byte[] bytes,
        byte expectedVersion,
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
        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedLength, length);
        Assert.Equal(expectedPrefixLength, prefixLength);
    }

    [Fact]
    public void TryReadVersionAndLength_RejectsMalformedLegacyVarUIntLength()
    {
        using var writer = CreateWriter([0x00]);
        using var buffer = writer.PeekSlice(writer.Length);
        Assert.Throws<InvalidOperationException>(() =>
            OrleansBinaryJournalReader.TryReadVersionAndLength(
                buffer,
                out _,
                out _,
                out _));
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
    [InlineData(new byte[] { 0x01 }, "truncated fixed-width entry header")]
    [InlineData(new byte[] { 0x00 }, "malformed varuint32 entry length prefix")]
    [InlineData(new byte[] { 0x0B, 1, 2 }, "exceeds remaining input bytes")]
    [InlineData(new byte[] { 0x03, 0x02 }, "truncated varuint state id")]
    [InlineData(
        new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        "malformed varuint state id")]
    public void BinaryFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(data.Reader, isCompleted: true);
            var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey);
            ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        });

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0 }, "truncated fixed-width entry header")]
    [InlineData(new byte[] { 1, 3, 0, 0, 0, 1, 0, 0 }, "smaller than the fixed-width state id")]
    [InlineData(new byte[] { 1, 8, 0, 0, 0, 1, 0, 0, 0, 0xAA }, "exceeds remaining input bytes")]
    public void BinaryFormat_Read_RejectsMalformedV1Frames(byte[] bytes, string expectedMessage)
    {
        using var data = CreateWriter(bytes);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new JournalBufferReader(data.Reader, isCompleted: true);
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

    private static ArcBufferWriter CreateLegacyWriter(JournalStreamId streamId, ReadOnlySpan<byte> payload)
    {
        var writer = new ArcBufferWriter();
        var serializerWriter = Writer.Create(writer, session: null!);
        serializerWriter.WriteVarUInt32(checked((uint)(GetVarUInt32ByteCount(streamId.Value) + payload.Length)));
        serializerWriter.WriteVarUInt64(streamId.Value);
        serializerWriter.Commit();
        writer.Write(payload);
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
        if (!OrleansBinaryJournalReader.TryReadVersionAndLength(remaining, out var version, out var length, out var lengthPrefixLength))
        {
            throw new InvalidOperationException("The binary journal entry stream is malformed.");
        }

        var entryStart = offset + lengthPrefixLength;
        if (length == 0 || length > input.Length - entryStart)
        {
            throw new InvalidOperationException("The binary journal entry stream is malformed.");
        }

        var entry = input.UnsafeSlice(entryStart, checked((int)length));
        if (version == OrleansBinaryJournalReader.FramingVersion)
        {
            var streamId = OrleansBinaryJournalReader.ReadUInt32LittleEndian(entry.UnsafeSlice(0, sizeof(uint)));
            var payload = entry.UnsafeSlice(sizeof(uint), entry.Length - sizeof(uint)).ToArray();
            offset = checked(entryStart + (int)length);
            return (length, streamId, payload);
        }

        var streamIdReader = Reader.Create(entry, session: null!);
        var legacyStreamId = checked((uint)streamIdReader.ReadVarUInt64());
        var payloadOffset = checked((int)streamIdReader.Position);
        var legacyPayload = entry.UnsafeSlice(payloadOffset, entry.Length - payloadOffset).ToArray();
        offset = checked(entryStart + (int)length);

        return (length, legacyStreamId, legacyPayload);
    }

    private static int GetVarUInt32ByteCount(uint value) => value switch
    {
        < 128u => 1,
        < 16_384u => 2,
        < 2_097_152u => 3,
        < 268_435_456u => 4,
        _ => 5
    };

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
