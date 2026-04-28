using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Protobuf;

internal sealed class ProtobufLogExtentCodec : IStateMachineLogExtentCodec
{
    private const uint RecordField = 1;
    private const uint StreamIdField = 1;
    private const uint EntryField = 2;

    public byte[] Encode(LogExtentBuilder value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var extent = new ArrayBufferWriter<byte>();
        foreach (var entry in value.Entries)
        {
            var record = new ArrayBufferWriter<byte>();
            ProtobufWire.WriteUInt64Field(record, StreamIdField, entry.StreamId.Value);
            ProtobufWire.WriteBytesField(record, EntryField, entry.Payload);
            ProtobufWire.WriteBytesField(extent, RecordField, record.WrittenSpan);
        }

        var output = new ArrayBufferWriter<byte>();
        ProtobufWire.WriteLengthDelimited(output, extent.WrittenSpan);
        return output.WrittenSpan.ToArray();
    }

    public LogExtent Decode(ArcBuffer value)
    {
        using (value)
        {
            var reader = new SequenceReader<byte>(value.AsReadOnlySequence());
            var entries = new List<LogExtent.Entry>();
            while (!reader.End)
            {
                DecodeExtent(ProtobufWire.ReadBytes(ref reader), entries);
            }

            return new LogExtent(entries);
        }
    }

    private static void DecodeExtent(ReadOnlySequence<byte> value, List<LogExtent.Entry> entries)
    {
        var reader = new SequenceReader<byte>(value);
        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case RecordField:
                    entries.Add(DecodeRecord(ProtobufWire.ReadBytes(ref reader)));
                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }
    }

    private static LogExtent.Entry DecodeRecord(ReadOnlySequence<byte> value)
    {
        var reader = new SequenceReader<byte>(value);
        var hasStreamId = false;
        var hasEntry = false;
        var streamId = 0UL;
        byte[]? entry = null;

        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case StreamIdField:
                    if (hasStreamId)
                    {
                        throw new InvalidOperationException("Malformed protobuf log extent: duplicate field 'stream_id'.");
                    }

                    streamId = ProtobufWire.ReadUInt64(ref reader);
                    hasStreamId = true;
                    break;
                case EntryField:
                    if (hasEntry)
                    {
                        throw new InvalidOperationException("Malformed protobuf log extent: duplicate field 'entry'.");
                    }

                    entry = ProtobufWire.ReadBytes(ref reader).ToArray();
                    hasEntry = true;
                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }

        if (!hasStreamId)
        {
            throw new InvalidOperationException("Malformed protobuf log extent: missing required field 'stream_id'.");
        }

        if (!hasEntry)
        {
            throw new InvalidOperationException("Malformed protobuf log extent: missing required field 'entry'.");
        }

        return new(new(streamId), new ReadOnlySequence<byte>(entry!));
    }
}
