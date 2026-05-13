using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryJournalFormat : IJournalFormat
{
    internal const string JournalFormatKey = "orleans-binary";

    private readonly SerializerSessionPool _sessionPool;

    public OrleansBinaryJournalFormat(SerializerSessionPool sessionPool)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);
        _sessionPool = sessionPool;
    }

    public string FormatKey => JournalFormatKey;

    public string? MimeType => "application/octet-stream";

    internal SerializerSessionPool SessionPool => _sessionPool;

    JournalBufferWriter IJournalFormat.CreateWriter() => new OrleansBinaryJournalBufferWriter();

    void IJournalFormat.Replay(JournalBufferReader input, JournalReplayContext context) =>
        OrleansBinaryJournalReader.Read(input, _sessionPool, context);
}

internal static class OrleansBinaryJournalReader
{
    internal const byte LegacyFramingVersion = 0;
    internal const byte FramingVersion = 1;

    private const int ByteCount = sizeof(byte);
    private const int UInt32ByteCount = sizeof(uint);
    private const int VersionedLengthPrefixLength = ByteCount + UInt32ByteCount;
    private const int MaxVarUInt32ByteCount = 5;

    public static void Read(JournalBufferReader input, SerializerSessionPool sessionPool, JournalReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);

        if (input.Length == 0)
        {
            return;
        }

        using var session = sessionPool.GetSession();
        var offset = 0L;

        while (input.Length > 0)
        {
            // Snapshot all currently-buffered bytes as a pinned ArcBuffer that the reader walks
            // for this iteration. Successful entries are committed by Skip()ing the underlying
            // ArcBufferReader; truncated tails leave bytes for a subsequent call.
            using var batchSlice = input.Peek(input.Length);
            var sequence = batchSlice.AsReadOnlySequence();

            byte framingVersion;
            uint bodyLength;
            uint streamIdValue;
            int frameLength;
            int payloadStart;
            int lengthPrefixSize;

            bool hasVersionAndLength;
            try
            {
                hasVersionAndLength = TryReadVersionAndLength(sequence, out framingVersion, out bodyLength, out lengthPrefixSize);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint32 entry length prefix.",
                    exception);
            }
            catch (NotSupportedException exception)
            {
                throw new NotSupportedException(
                    $"Unsupported binary journal entry format version at byte offset {offset}: {exception.Message}",
                    exception);
            }

            if (!hasVersionAndLength)
            {
                if (!input.IsCompleted)
                {
                    return;
                }

                var message = framingVersion == FramingVersion
                    ? "truncated fixed-width entry header"
                    : "truncated varuint32 entry length prefix";
                throw new InvalidOperationException($"Malformed binary journal entry stream at byte offset {offset}: {message}.");
            }

            if (framingVersion == FramingVersion)
            {
                if (bodyLength < UInt32ByteCount)
                {
                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} is smaller than the fixed-width state id.");
                }

                var availableBody = batchSlice.Length - lengthPrefixSize;
                if (bodyLength > (ulong)availableBody)
                {
                    if (!input.IsCompleted)
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {availableBody}.");
                }

                frameLength = checked(lengthPrefixSize + (int)bodyLength);
                streamIdValue = ReadUInt32LittleEndian(sequence.Slice(lengthPrefixSize, UInt32ByteCount));
                payloadStart = lengthPrefixSize + UInt32ByteCount;
            }
            else
            {
                if (bodyLength == 0)
                {
                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: zero-length entries are not valid.");
                }

                var availableBody = batchSlice.Length - lengthPrefixSize;
                if (bodyLength > (ulong)availableBody)
                {
                    if (!input.IsCompleted)
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {availableBody}.");
                }

                frameLength = checked(lengthPrefixSize + (int)bodyLength);
                var entry = sequence.Slice(lengthPrefixSize, bodyLength);
                var reader = Reader.Create(entry, session);

                try
                {
                    streamIdValue = reader.ReadVarUInt32();
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint32 state id.",
                        exception);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint32 state id.",
                        exception);
                }

                payloadStart = lengthPrefixSize + (int)reader.Position;
            }

            var streamId = new JournalStreamId(streamIdValue);
            var state = context.ResolveState(streamId);

            // Slice the entry payload (post-streamId) so the state receives exactly one command body.
            var payloadLength = frameLength - payloadStart;
            using var payloadSlice = batchSlice.Slice(payloadStart, payloadLength);
            using var payloadBuffer = new ArcBufferWriter();
            payloadBuffer.AppendPinned(payloadSlice);

            try
            {
                state.ReplayEntry(
                    new JournalEntry(OrleansBinaryJournalFormat.JournalFormatKey, new JournalBufferReader(new ArcBufferReader(payloadBuffer), isCompleted: true)),
                    context);
            }
            catch (Exception exception) when (exception is not InvalidOperationException ioe || !ioe.Message.StartsWith("Malformed binary journal entry stream", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Failed to apply binary journal entry at byte offset {offset} for stream {streamIdValue}: {exception.Message}",
                    exception);
            }

            input.Skip(frameLength);
            offset += frameLength;
        }
    }

    internal static bool TryReadVersionAndLength(
        ReadOnlySequence<byte> input,
        out byte version,
        out uint length,
        out int lengthPrefixLength)
    {
        version = LegacyFramingVersion;
        length = 0;
        lengthPrefixLength = 0;

        var reader = new SequenceReader<byte>(input);
        if (!reader.TryRead(out var firstByte))
        {
            return false;
        }

        if (firstByte == FramingVersion)
        {
            version = FramingVersion;
            if (reader.Remaining < UInt32ByteCount)
            {
                return false;
            }

            Span<byte> lengthBytes = stackalloc byte[UInt32ByteCount];
            reader.TryCopyTo(lengthBytes);
            length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            lengthPrefixLength = VersionedLengthPrefixLength;
            return true;
        }

        return TryReadLegacyLength(input, out length, out lengthPrefixLength);
    }

    private static bool TryReadLegacyLength(ReadOnlySequence<byte> input, out uint length, out int bytesRead)
    {
        length = 0;
        bytesRead = 0;

        var reader = new SequenceReader<byte>(input);
        if (!reader.TryRead(out var firstByte))
        {
            return false;
        }

        // Orleans varuints encode their width as the number of trailing zero bits in the first byte.
        var byteCount = 1;
        var marker = firstByte;
        while ((marker & 1) == 0)
        {
            if (byteCount == MaxVarUInt32ByteCount)
            {
                throw new InvalidOperationException("Malformed varuint32 entry length prefix.");
            }

            byteCount++;
            marker >>= 1;
        }

        if (input.Length < byteCount)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[MaxVarUInt32ByteCount];
        input.Slice(0, byteCount).CopyTo(buffer);

        ulong result = 0;
        for (var i = 0; i < byteCount; i++)
        {
            result |= (ulong)buffer[i] << (i * 8);
        }

        result >>= byteCount;
        if (result > uint.MaxValue)
        {
            throw new InvalidOperationException("Malformed varuint32 entry length prefix.");
        }

        length = (uint)result;
        bytesRead = byteCount;
        return true;
    }

    internal static uint ReadUInt32LittleEndian(ReadOnlySequence<byte> input)
    {
        Span<byte> buffer = stackalloc byte[UInt32ByteCount];
        input.Slice(0, UInt32ByteCount).CopyTo(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
