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
    internal const byte LegacyFramingVersion = OrleansBinaryV0JournalReader.FramingVersion;
    internal const byte FramingVersion = 1;

    private const int ByteCount = sizeof(byte);
    private const int UInt32ByteCount = sizeof(uint);
    private const int VersionedLengthPrefixLength = ByteCount + UInt32ByteCount;

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

            if (!TryReadEntry(
                batchSlice,
                input.IsCompleted,
                session,
                offset,
                out var streamIdValue,
                out var frameLength,
                out var payloadStart))
            {
                return;
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
                    new JournalEntry(OrleansBinaryJournalFormat.JournalFormatKey, new JournalBufferReader(payloadBuffer.Reader, isCompleted: true)),
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

    private static bool TryReadEntry(
        ArcBuffer input,
        bool isCompleted,
        SerializerSession session,
        long offset,
        out uint streamIdValue,
        out int frameLength,
        out int payloadStart)
    {
        streamIdValue = 0;
        frameLength = 0;
        payloadStart = 0;

        byte framingVersion;
        uint bodyLength;
        int lengthPrefixSize;
        bool hasVersionAndLength;
        try
        {
            hasVersionAndLength = TryReadVersionAndLength(input, session, out framingVersion, out bodyLength, out lengthPrefixSize);
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
            if (!isCompleted)
            {
                return false;
            }

            var message = framingVersion == FramingVersion
                ? "truncated fixed-width entry header"
                : "truncated varuint32 entry length prefix";
            throw new InvalidOperationException($"Malformed binary journal entry stream at byte offset {offset}: {message}.");
        }

        return framingVersion == FramingVersion
            ? TryReadCurrentEntry(input, lengthPrefixSize, bodyLength, isCompleted, session, offset, out streamIdValue, out frameLength, out payloadStart)
            : OrleansBinaryV0JournalReader.TryReadEntry(input, lengthPrefixSize, bodyLength, isCompleted, session, offset, out streamIdValue, out frameLength, out payloadStart);
    }

    internal static bool TryReadVersionAndLength(
        ArcBuffer input,
        out byte version,
        out uint length,
        out int lengthPrefixLength) =>
        TryReadVersionAndLength(input, session: null!, out version, out length, out lengthPrefixLength);

    private static bool TryReadVersionAndLength(
        ArcBuffer input,
        SerializerSession session,
        out byte version,
        out uint length,
        out int lengthPrefixLength)
    {
        version = LegacyFramingVersion;
        length = 0;
        lengthPrefixLength = 0;

        if (input.Length == 0)
        {
            return false;
        }

        var reader = Reader.Create(input, session);
        var firstByte = reader.ReadByte();
        if (firstByte == FramingVersion)
        {
            version = FramingVersion;
            if (input.Length < VersionedLengthPrefixLength)
            {
                return false;
            }

            length = reader.ReadUInt32();
            lengthPrefixLength = checked((int)reader.Position);
            return true;
        }

        return OrleansBinaryV0JournalReader.TryReadLength(input, session, out length, out lengthPrefixLength);
    }

    private static bool TryReadCurrentEntry(
        ArcBuffer input,
        int lengthPrefixSize,
        uint bodyLength,
        bool isCompleted,
        SerializerSession session,
        long offset,
        out uint streamIdValue,
        out int frameLength,
        out int payloadStart)
    {
        streamIdValue = 0;
        frameLength = 0;
        payloadStart = 0;

        if (bodyLength < UInt32ByteCount)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} is smaller than the fixed-width state id.");
        }

        var availableBody = input.Length - lengthPrefixSize;
        if (bodyLength > (ulong)availableBody)
        {
            if (!isCompleted)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {availableBody}.");
        }

        frameLength = checked(lengthPrefixSize + (int)bodyLength);
        streamIdValue = ReadUInt32LittleEndian(input.UnsafeSlice(lengthPrefixSize, UInt32ByteCount), session);
        payloadStart = lengthPrefixSize + UInt32ByteCount;
        return true;
    }

    internal static uint ReadUInt32LittleEndian(ArcBuffer input) => ReadUInt32LittleEndian(input, session: null!);

    private static uint ReadUInt32LittleEndian(ArcBuffer input, SerializerSession session) => Reader.Create(input, session).ReadUInt32();
}
