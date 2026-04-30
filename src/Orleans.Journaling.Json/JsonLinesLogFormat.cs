using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesLogFormat : ILogFormat
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> EntryPrefixStart => "{\"streamId\":"u8;
    private static ReadOnlySpan<byte> EntryPrefixEnd => ",\"entry\":"u8;
    private static ReadOnlySpan<byte> EntrySuffix => "}\n"u8;
    private static ReadOnlySpan<byte> StreamIdPropertyNameUtf8 => "streamId"u8;
    private static ReadOnlySpan<byte> EntryPropertyNameUtf8 => "entry"u8;
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';
    private const string StreamIdPropertyName = "streamId";
    private const string EntryPropertyName = "entry";

    public ILogSegmentWriter CreateWriter() => new JsonLinesLogSegmentWriter();

    public bool TryRead(ArcBufferReader input, ILogEntrySink consumer, bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (input.Length == 0)
        {
            return false;
        }

        using var buffered = input.PeekSlice(input.Length);
        var sequence = buffered.AsReadOnlySequence();
        if (StartsWithBom(sequence))
        {
            throw new InvalidOperationException("Malformed JSON Lines log segment: UTF-8 byte order marks are not supported.");
        }

        var reader = new SequenceReader<byte>(sequence);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, LineFeed, advancePastDelimiter: true))
        {
            if (!isCompleted)
            {
                return false;
            }

            throw new InvalidOperationException("Malformed JSON Lines log segment at byte offset 0: log entries must end with a newline.");
        }

        var consumed = reader.Consumed;
        if (line.Length > int.MaxValue || consumed > int.MaxValue)
        {
            throw new InvalidOperationException("Malformed JSON Lines log segment: log entry exceeds maximum supported frame size.");
        }

        input.Skip(checked((int)consumed));
        if (!line.IsEmpty && EndsWith(line, CarriageReturn))
        {
            line = line.Slice(0, line.Length - 1);
        }

        if (IsBlankLine(line))
        {
            throw new InvalidOperationException("Malformed JSON Lines log segment at byte offset 0: blank lines are not valid log entries.");
        }

        ReadLine(line, offset: 0, consumer);
        return true;
    }

    private static void ReadLine(ReadOnlySequence<byte> line, long offset, ILogEntrySink consumer)
    {
        var reader = new Utf8JsonReader(line);
        var hasStreamId = false;
        var hasEntry = false;
        ulong streamId = 0;
        long entryStart = 0;
        long entryLength = 0;

        try
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: blank lines are not valid log entries.");
            }

            if (reader.TokenType is not JsonTokenType.StartObject)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must be a JSON object.");
            }

            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType is not JsonTokenType.PropertyName)
                {
                    throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: expected a property name.");
                }

                if (reader.ValueTextEquals(StreamIdPropertyNameUtf8))
                {
                    if (hasStreamId)
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: duplicate property '{StreamIdPropertyName}'.");
                    }

                    if (!reader.Read())
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing value for property '{StreamIdPropertyName}'.");
                    }

                    if (reader.TokenType is not JsonTokenType.Number || !reader.TryGetUInt64(out streamId))
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: property '{StreamIdPropertyName}' must be an unsigned integer.");
                    }

                    hasStreamId = true;
                }
                else if (reader.ValueTextEquals(EntryPropertyNameUtf8))
                {
                    if (hasEntry)
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: duplicate property '{EntryPropertyName}'.");
                    }

                    if (!reader.Read())
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing value for property '{EntryPropertyName}'.");
                    }

                    if (reader.TokenType is not JsonTokenType.StartObject)
                    {
                        throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: property '{EntryPropertyName}' must be a JSON object.");
                    }

                    entryStart = reader.TokenStartIndex;
                    reader.Skip();
                    entryLength = reader.BytesConsumed - entryStart;
                    hasEntry = true;
                }
                else
                {
                    var propertyName = reader.GetString();
                    throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: unexpected property '{propertyName}'.");
                }
            }

            if (reader.TokenType is not JsonTokenType.EndObject)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must contain one complete JSON object.");
            }

            if (!hasStreamId)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing required property '{StreamIdPropertyName}'.");
            }

            if (!hasEntry)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing required property '{EntryPropertyName}'.");
            }

            if (reader.Read())
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: trailing JSON content after the log entry object.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: invalid JSON log entry.", exception);
        }

        consumer.OnEntry(new(streamId), line.Slice(entryStart, entryLength));
    }

    private static bool StartsWithBom(ReadOnlySequence<byte> input)
    {
        if (input.Length < Bom.Length)
        {
            return false;
        }

        Span<byte> prefix = stackalloc byte[3];
        input.Slice(0, Bom.Length).CopyTo(prefix);
        return prefix.SequenceEqual(Bom);
    }

    private static bool EndsWith(ReadOnlySequence<byte> input, byte value)
    {
        if (input.IsSingleSegment)
        {
            return input.FirstSpan[^1] == value;
        }

        var reader = new SequenceReader<byte>(input.Slice(input.Length - 1));
        return reader.TryRead(out var last) && last == value;
    }

    private static bool IsBlankLine(ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(line);
        while (reader.TryRead(out var value))
        {
            if (value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class JsonLinesLogSegmentWriter : LogSegmentWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private int _activePayloadStart;

        public override long Length => _buffer.Length;

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The JSON Lines log segment has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The JSON Lines log segment cannot be reset while an entry is active.");
            }

            _activePayloadStart = 0;
            _buffer.Reset();
        }

        public override void Dispose() => _buffer.Dispose();

        protected override int GetEntryStart(LogStreamId streamId)
        {
            var entryStart = _buffer.Length;
            _buffer.Write(EntryPrefixStart);

            Span<byte> streamIdBytes = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(streamId.Value, streamIdBytes, out var bytesWritten))
            {
                throw new InvalidOperationException("The JSON Lines log segment could not format the state-machine id.");
            }

            _buffer.Write(streamIdBytes[..bytesWritten]);
            _buffer.Write(EntryPrefixEnd);
            _activePayloadStart = _buffer.Length;
            return entryStart;
        }

        protected override void AdvancePayload(int count) => _buffer.AdvanceWriter(count);

        protected override Memory<byte> GetPayloadMemory(int sizeHint) => _buffer.GetMemory(sizeHint);

        protected override Span<byte> GetPayloadSpan(int sizeHint) => _buffer.GetSpan(sizeHint);

        protected override void WritePayload(ReadOnlySpan<byte> value) => _buffer.Write(value);

        protected override void WritePayload(ReadOnlySequence<byte> value) => _buffer.Write(value);

        protected override void CommitEntry(LogStreamId streamId, int entryStart)
        {
            if (_buffer.Length == _activePayloadStart)
            {
                throw new InvalidOperationException("The JSON Lines log entry has no entry payload.");
            }

            _buffer.Write(EntrySuffix);
            _activePayloadStart = 0;
        }

        protected override void AbortEntry(LogStreamId streamId, int entryStart)
        {
            _activePayloadStart = 0;
            _buffer.Truncate(entryStart);
        }
    }
}
