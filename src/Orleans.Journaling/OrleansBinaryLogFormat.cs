using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryLogFormat : ILogFormat
{
    internal const string LogFormatKey = "orleans-binary";

    public static OrleansBinaryLogFormat Instance { get; } = new();

    private OrleansBinaryLogFormat()
    {
    }

    ILogBatchWriter ILogFormat.CreateWriter() => new OrleansBinaryLogBatchWriter();

    bool ILogFormat.TryRead(ArcBufferReader input, IStateMachineResolver resolver, bool isCompleted) => OrleansBinaryLogReader.TryRead(input, resolver, isCompleted);
}

internal static class OrleansBinaryLogReader
{
    public static bool TryRead(ArcBufferReader input, IStateMachineResolver resolver, bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (input.Length == 0)
        {
            return false;
        }

        if (input.Length < OrleansBinaryLogEntryFrameReader.LengthPrefixSize)
        {
            if (!isCompleted)
            {
                return false;
            }

            throw new InvalidOperationException(
                "Malformed binary log entry stream at byte offset 0: truncated fixed32 entry length prefix.");
        }

        Span<byte> lengthBytes = stackalloc byte[OrleansBinaryLogEntryFrameReader.LengthPrefixSize];
        var lengthPrefix = input.Peek(lengthBytes);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthPrefix[..OrleansBinaryLogEntryFrameReader.LengthPrefixSize]);
        var frameLength = checked(OrleansBinaryLogEntryFrameReader.LengthPrefixSize + (long)bodyLength);
        if (input.Length < frameLength)
        {
            if (!isCompleted)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset 0: entry length {bodyLength} exceeds remaining input bytes {input.Length - OrleansBinaryLogEntryFrameReader.LengthPrefixSize}.");
        }

        if (frameLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Malformed binary log entry stream at byte offset 0: entry length exceeds maximum supported frame size.");
        }

        using var frame = input.PeekSlice((int)frameLength);
        var remaining = frame.AsReadOnlySequence();
        if (!OrleansBinaryLogEntryFrameReader.TryReadEntry(ref remaining, offset: 0, isCompleted: true, out var streamId, out var payload, out _, out _))
        {
            throw new InvalidOperationException("The binary log format failed to read a complete frame.");
        }

        input.Skip((int)frameLength);
        var stateMachine = resolver.ResolveStateMachine(streamId);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new OrleansBinaryFormattedLogEntry(payload));
        }
        else
        {
            stateMachine.Apply(payload);
        }

        return true;
    }
}

internal static class OrleansBinaryLogEntryFrameReader
{
    public const int LengthPrefixSize = sizeof(uint);
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
