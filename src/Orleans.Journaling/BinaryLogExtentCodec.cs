using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class BinaryLogExtentCodec : IStateMachineLogFormat
{
    public static BinaryLogExtentCodec Instance { get; } = new();

    private BinaryLogExtentCodec()
    {
    }

    IStateMachineLogExtentWriter IStateMachineLogFormat.CreateWriter() => new LogExtentBuffer();

    void IStateMachineLogFormat.Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer) => BinaryLogExtentReader.Read(input, consumer);
}

internal static class BinaryLogExtentReader
{
    public static void Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        var remaining = input.AsReadOnlySequence();
        var offset = 0L;
        while (BinaryLogEntryFrameReader.TryReadEntry(ref remaining, offset, out var streamId, out var payload, out var frameLength))
        {
            consumer.OnEntry(streamId, payload);
            remaining = remaining.Slice(frameLength);
            offset += frameLength;
        }
    }
}

internal static class BinaryLogEntryFrameReader
{
    private const int LengthPrefixSize = sizeof(uint);
    private const int MaxVarUInt64Bytes = 10;

    public static bool TryReadEntry(
        ref ReadOnlySequence<byte> remaining,
        long offset,
        out StateMachineId streamId,
        out ReadOnlySequence<byte> payload,
        out long frameLength)
    {
        streamId = default;
        payload = default;
        frameLength = 0;

        if (remaining.IsEmpty)
        {
            return false;
        }

        if (remaining.Length < LengthPrefixSize)
        {
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
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {reader.Remaining}.");
        }

        var body = remaining.Slice(LengthPrefixSize, bodyLength);
        var bodyReader = new SequenceReader<byte>(body);
        var id = ReadStateMachineId(ref bodyReader, offset);
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

    private static ulong ReadStateMachineId(ref SequenceReader<byte> reader, long offset)
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
