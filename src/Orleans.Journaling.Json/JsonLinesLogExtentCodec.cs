using System.Buffers;
using System.Text;
using System.Text.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesLogExtentCodec : IStateMachineLogExtentCodec
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';
    private const string RecordsPropertyName = "records";
    private const string StreamIdPropertyName = "streamId";
    private const string EntryPropertyName = "entry";

    public byte[] Encode(LogExtentBuilder value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(RecordsPropertyName);
            writer.WriteStartArray();
            foreach (var entry in value.Entries)
            {
                writer.WriteStartObject();
                writer.WriteNumber(StreamIdPropertyName, entry.StreamId.Value);
                writer.WritePropertyName(EntryPropertyName);
                using (var document = JsonDocument.Parse(entry.Payload))
                {
                    document.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        WriteByte(output, LineFeed);
        return output.WrittenSpan.ToArray();
    }

    public LogExtent Decode(ArcBuffer value)
    {
        using (value)
        {
            var bytes = value.ToArray();
            if (bytes.AsSpan().StartsWith(Bom))
            {
                throw new InvalidOperationException("Malformed JSON Lines log extent: UTF-8 byte order marks are not supported.");
            }

            var entries = new List<LogExtent.Entry>();
            var start = 0;
            while (start < bytes.Length)
            {
                var end = Array.IndexOf(bytes, LineFeed, start);
                if (end < 0)
                {
                    end = bytes.Length;
                }

                var line = bytes.AsMemory(start, end - start);
                if (line.Length > 0 && line.Span[^1] == CarriageReturn)
                {
                    line = line[..^1];
                }

                if (line.Length == 0)
                {
                    throw new InvalidOperationException("Malformed JSON Lines log extent: blank lines are not valid log entries.");
                }

                DecodeLine(line, entries);

                start = end + 1;
            }

            return new LogExtent(entries);
        }
    }

    private static void DecodeLine(ReadOnlyMemory<byte> line, List<LogExtent.Entry> entries)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException("Malformed JSON Lines log extent: each line must be a JSON object.");
        }

        if (!root.TryGetProperty(RecordsPropertyName, out var recordsElement))
        {
            throw new InvalidOperationException($"Malformed JSON Lines log extent: missing required property '{RecordsPropertyName}'.");
        }

        if (recordsElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log extent: property '{RecordsPropertyName}' must be a JSON array.");
        }

        foreach (var recordElement in recordsElement.EnumerateArray())
        {
            if (recordElement.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidOperationException("Malformed JSON Lines log extent: each record must be a JSON object.");
            }

            if (!recordElement.TryGetProperty(StreamIdPropertyName, out var streamIdElement))
            {
                throw new InvalidOperationException($"Malformed JSON Lines log extent: missing required property '{StreamIdPropertyName}'.");
            }

            if (!recordElement.TryGetProperty(EntryPropertyName, out var entryElement))
            {
                throw new InvalidOperationException($"Malformed JSON Lines log extent: missing required property '{EntryPropertyName}'.");
            }

            if (entryElement.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log extent: property '{EntryPropertyName}' must be a JSON object.");
            }

            var payload = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(payload, WriterOptions))
            {
                entryElement.WriteTo(writer);
            }

            entries.Add(new(new(streamIdElement.GetUInt64()), new ReadOnlySequence<byte>(payload.WrittenMemory)));
        }
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }
}
