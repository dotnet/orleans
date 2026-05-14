using System.Buffers;
using System.Buffers.Binary;

namespace Orleans.Journaling;

internal class OrleansBinaryJournalBufferWriter : JournalBufferWriter
{
    private const int ByteCount = sizeof(byte);
    private const int UInt32ByteCount = sizeof(uint);
    private const int VersionedLengthPrefixLength = ByteCount + UInt32ByteCount;

    protected override void StartEntry(JournalStreamId streamId)
    {
        WriteEntryHeader(Output, streamId, 0);
    }

    protected override void FinishEntry(JournalStreamId streamId)
    {
        var length = checked((uint)(ActiveEntryLength - VersionedLengthPrefixLength));
        WriteLength(ActiveEntryStart, length);
    }

    protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
    {
        if (!string.Equals(entry.FormatKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Orleans binary journal buffer writer cannot append preserved entry for journal format key '{entry.FormatKey}'.");
        }

        var length = checked((uint)(UInt32ByteCount + entry.Payload.Length));
        WriteEntryHeader(Output, streamId, length);
        Output.Write(entry.Payload.Span);
    }

    private static void WriteEntryHeader(IBufferWriter<byte> output, JournalStreamId streamId, uint bodyLength)
    {
        WriteByte(output, OrleansBinaryJournalReader.FramingVersion);
        WriteUInt32(output, bodyLength);
        WriteUInt32(output, streamId.Value);
    }

    private void WriteLength(int entryStart, uint length)
    {
        Span<byte> encoded = stackalloc byte[UInt32ByteCount];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, length);
        WriteAt(entryStart + ByteCount, encoded);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var destination = output.GetSpan(ByteCount);
        destination[0] = value;
        output.Advance(ByteCount);
    }

    private static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        var destination = output.GetSpan(UInt32ByteCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        output.Advance(UInt32ByteCount);
    }
}
