using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal static class OrleansBinaryV0JournalReader
{
    internal const byte FramingVersion = 0;

    private const byte CommandFormatVersion = 0;
    private const int MaxVarUInt32ByteCount = 5;

    internal static bool TryReadLength(ArcBuffer input, SerializerSession session, out uint length, out int bytesRead)
    {
        length = 0;
        bytesRead = 0;

        if (input.Length == 0)
        {
            return false;
        }

        var reader = Reader.Create(input, session);
        var byteCount = Reader.GetVarIntByteCount(reader.PeekByte());
        if (byteCount > MaxVarUInt32ByteCount)
        {
            throw new InvalidOperationException("Malformed varuint32 entry length prefix.");
        }

        if (input.Length < byteCount)
        {
            return false;
        }

        try
        {
            length = reader.ReadVarUInt32();
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("Malformed varuint32 entry length prefix.", exception);
        }

        bytesRead = checked((int)reader.Position);
        return true;
    }

    internal static bool TryReadEntry(
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

        if (bodyLength == 0)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: zero-length entries are not valid.");
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

        var entryStart = lengthPrefixSize;
        frameLength = checked(entryStart + (int)bodyLength);
        var entry = input.UnsafeSlice(entryStart, (int)bodyLength);
        var payloadOffset = ReadEntryHeader(entry, session, offset, out streamIdValue);
        payloadStart = entryStart + payloadOffset;
        return true;
    }

    private static int ReadEntryHeader(
        ArcBuffer entry,
        SerializerSession session,
        long offset,
        out uint streamIdValue)
    {
        var reader = Reader.Create(entry, session);

        ulong streamId;
        try
        {
            streamId = reader.ReadVarUInt64();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint state id.",
                exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint state id.",
                exception);
        }

        if (streamId > uint.MaxValue)
        {
            throw new NotSupportedException(
                $"Unsupported legacy binary journal stream id at byte offset {offset}: {streamId}.");
        }

        streamIdValue = (uint)streamId;

        if (reader.Position >= entry.Length)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: missing legacy command format version.");
        }

        var commandVersion = reader.ReadByte();
        if (commandVersion != CommandFormatVersion)
        {
            throw new NotSupportedException(
                $"Unsupported legacy binary journal command format version at byte offset {offset}: {commandVersion}.");
        }

        return checked((int)reader.Position);
    }
}
