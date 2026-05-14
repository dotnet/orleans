using System.Buffers;
using System.Buffers.Binary;

namespace Orleans.Journaling;

internal class OrleansBinaryJournalBufferWriter : JournalBufferWriter
{
    private const int VersionHeaderLength = sizeof(byte);
    private const int LengthFieldOffset = VersionHeaderLength;
    private const int LengthFieldLength = sizeof(uint);
    private const int FrameHeaderLength = VersionHeaderLength + LengthFieldLength;
    private const int StreamIdLength = sizeof(uint);

    protected override void StartEntry(JournalStreamId streamId) => WriteEntryHeader(Output, streamId, 0);

    protected override void FinishEntry(JournalStreamId streamId)
    {
        var length = checked((uint)(ActiveEntryLength - FrameHeaderLength));
        Span<byte> encoded = stackalloc byte[LengthFieldLength];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, length);
        WriteAt(LengthFieldOffset, encoded);
    }

    protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
    {
        if (!string.Equals(entry.FormatKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Orleans binary journal buffer writer cannot append preserved entry for journal format key '{entry.FormatKey}'.");
        }

        var length = checked((uint)(StreamIdLength + entry.Payload.Length));
        WriteEntryHeader(Output, streamId, length);
        Output.Write(entry.Payload.Span);
    }

    private static void WriteEntryHeader(IBufferWriter<byte> output, JournalStreamId streamId, uint bodyLength)
    {
        WriteByte(output, OrleansBinaryJournalReader.FramingVersion);
        WriteUInt32(output, bodyLength);
        WriteUInt32(output, streamId.Value);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var destination = output.GetSpan(sizeof(byte));
        destination[0] = value;
        output.Advance(sizeof(byte));
    }

    private static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        var destination = output.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        output.Advance(sizeof(uint));
    }
}
