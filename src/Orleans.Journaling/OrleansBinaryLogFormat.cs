using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryLogFormat : ILogFormat
{
    public static OrleansBinaryLogFormat Instance { get; } = new();

    private OrleansBinaryLogFormat()
    {
    }

    ILogSegmentWriter ILogFormat.CreateWriter() => new LogSegmentBuffer();

    LogFormatReadResult ILogFormat.Read(ArcBuffer input, ILogEntrySink sink, bool isCompleted) => OrleansBinaryLogReader.Read(input, sink, isCompleted);
}

internal static class OrleansBinaryLogReader
{
    public static LogFormatReadResult Read(ArcBuffer input, ILogEntrySink sink, bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var remaining = input.AsReadOnlySequence();
        var offset = 0L;
        int? minimumBufferLength = null;
        while (OrleansBinaryLogEntryFrameReader.TryReadEntry(ref remaining, offset, isCompleted, out var streamId, out var payload, out var frameLength, out minimumBufferLength))
        {
            sink.OnEntry(streamId, payload);
            remaining = remaining.Slice(frameLength);
            offset += frameLength;
        }

        return new(checked((int)offset), remaining.IsEmpty ? null : OrleansBinaryLogEntryFrameReader.GetMinimumBufferLength(remaining, minimumBufferLength));
    }
}

internal static class OrleansBinaryLogEntryFrameReader
{
    private const int LengthPrefixSize = sizeof(uint);
    private const int MaxVarUInt64Bytes = 10;

    public static bool TryReadEntry(
        ref ReadOnlySequence<byte> remaining,
        long offset,
        bool isCompleted,
        out LogStreamId streamId,
        out ReadOnlySequence<byte> payload,
        out long frameLength,
        out int? minimumBufferLength)
    {
        streamId = default;
        payload = default;
        frameLength = 0;
        minimumBufferLength = null;

        if (remaining.IsEmpty)
        {
            return false;
        }

        if (remaining.Length < LengthPrefixSize)
        {
            if (!isCompleted)
            {
                minimumBufferLength = LengthPrefixSize;
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: truncated fixed32 entry length prefix.");
        }

        Span<byte> lengthBytes = stackalloc byte[LengthPrefixSize];
        var reader = new SequenceReader<byte>(remaining);
        if (!reader.TryCopyTo(lengthBytes))
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: truncated fixed32 entry length prefix.");
        }

        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (bodyLength == 0)
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: zero-length entries are not valid.");
        }

        reader.Advance(LengthPrefixSize);
        if (bodyLength > (ulong)reader.Remaining)
        {
            if (!isCompleted)
            {
                minimumBufferLength = bodyLength <= int.MaxValue - LengthPrefixSize ? LengthPrefixSize + checked((int)bodyLength) : null;
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {reader.Remaining}.");
        }

        var body = remaining.Slice(LengthPrefixSize, bodyLength);
        var bodyReader = new SequenceReader<byte>(body);
        var id = ReadLogStreamId(ref bodyReader, offset);
        payload = body.Slice(bodyReader.Consumed);
        if (payload.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: missing operation payload.");
        }

        streamId = new(id);
        frameLength = checked(LengthPrefixSize + (long)bodyLength);
        return true;
    }

    public static int? GetMinimumBufferLength(ReadOnlySequence<byte> remaining, int? minimumBufferLength)
        => minimumBufferLength ?? (remaining.Length > int.MaxValue ? null : checked((int)remaining.Length + 1));

    private static ulong ReadLogStreamId(ref SequenceReader<byte> reader, long offset)
    {
        ulong result = 0;
        for (var index = 0; index < MaxVarUInt64Bytes; index++)
        {
            if (!reader.TryRead(out var value))
            {
                throw new InvalidOperationException(
                    $"Malformed binary log entry stream at byte offset {offset}: truncated varuint64 state-machine id.");
            }

            var valueBits = value & 0x7F;
            if (index == MaxVarUInt64Bytes - 1 && (valueBits > 1 || (value & 0x80) != 0))
            {
                throw new InvalidOperationException(
                    $"Malformed binary log entry stream at byte offset {offset}: malformed varuint64 state-machine id.");
            }

            result |= (ulong)valueBits << (index * 7);
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            $"Malformed binary log entry stream at byte offset {offset}: malformed varuint64 state-machine id.");
    }
}
