using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class LogExtentBufferTests
{
    [Fact]
    public void Commit_WritesFixed32FramedEntry()
    {
        using var buffer = new LogExtentBuffer();

        var writer = buffer.BeginEntry(new StateMachineId(42));
        writer.Write([1, 2, 3]);
        writer.Commit();

        var bytes = ToArray(buffer);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(42, bytes[4]);
        Assert.Equal([1, 2, 3], bytes[5..]);
    }

    [Fact]
    public void Commit_WritesMultipleEntries()
    {
        using var buffer = new LogExtentBuffer();

        var first = buffer.BeginEntry(new StateMachineId(1));
        first.Write([10]);
        first.Commit();

        var second = buffer.BeginEntry(new StateMachineId(300));
        second.Write([20, 21]);
        second.Commit();

        var reader = new SequenceReader<byte>(buffer.AsReadOnlySequence());
        var firstEntry = ReadEntry(ref reader);
        var secondEntry = ReadEntry(ref reader);

        Assert.Equal(1UL, firstEntry.StreamId);
        Assert.Equal([10], firstEntry.Payload.ToArray());
        Assert.Equal(300UL, secondEntry.StreamId);
        Assert.Equal([20, 21], secondEntry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void Abort_TruncatesPendingEntry()
    {
        using var buffer = new LogExtentBuffer();

        var committed = buffer.BeginEntry(new StateMachineId(1));
        committed.Write([1]);
        committed.Commit();
        var committedBytes = ToArray(buffer);

        var aborted = buffer.BeginEntry(new StateMachineId(2));
        aborted.Write([2, 3, 4]);
        aborted.Abort();

        Assert.Equal(committedBytes, ToArray(buffer));
    }

    [Fact]
    public void Reset_ReusesBuffer()
    {
        using var buffer = new LogExtentBuffer();

        var first = buffer.BeginEntry(new StateMachineId(1));
        first.Write([1]);
        first.Commit();
        buffer.Reset();

        var second = buffer.BeginEntry(new StateMachineId(2));
        second.Write([2]);
        second.Commit();

        var reader = new SequenceReader<byte>(buffer.AsReadOnlySequence());
        var entry = ReadEntry(ref reader);

        Assert.Equal(2UL, entry.StreamId);
        Assert.Equal([2], entry.Payload.ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void Commit_BackpatchesLengthAcrossSegments()
    {
        using var buffer = new LogExtentBuffer();
        buffer.Write(new byte[ArcBufferWriter.MinimumPageSize - 2]);

        var writer = buffer.BeginEntry(new StateMachineId(1));
        writer.Write([42]);
        writer.Commit();

        var bytes = ToArray(buffer);
        var offset = ArcBufferWriter.MinimumPageSize - 2;

        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
        Assert.Equal(1, bytes[offset + 4]);
        Assert.Equal(42, bytes[offset + 5]);
    }

    [Fact]
    public void ReadOnlyStream_ReadsCommittedBytes()
    {
        using var buffer = new LogExtentBuffer();
        var writer = buffer.BeginEntry(new StateMachineId(7));
        writer.Write([8, 9]);
        writer.Commit();
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

    private static byte[] ToArray(LogExtentBuffer buffer)
    {
        using var slice = buffer.PeekSlice();
        return slice.ToArray();
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
}
