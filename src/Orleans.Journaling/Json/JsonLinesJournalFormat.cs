using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Orleans.Journaling.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesJournalFormat : IJournalFormat
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    public string FormatKey => JsonJournalExtensions.JournalFormatKey;

    public string? MimeType => "application/jsonl";

    public JournalWriter CreateWriter() => new JsonLinesJournalWriter();

    public void Read(JournalReadBuffer input, IStateResolver resolver, in JournaledStateReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var offset = 0L;
        while (TryReadLine(input, resolver, in context, ref offset))
        {
        }
    }

    private static bool TryReadLine(JournalReadBuffer input, IStateResolver resolver, in JournaledStateReplayContext context, ref long offset)
    {
        if (input.Length == 0)
        {
            return false;
        }

        if (input.IsNext(Bom))
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: UTF-8 byte order marks are not supported.");
        }

        if (!input.TryReadTo(out var lineBuffer, LineFeed, advancePastDelimiter: true))
        {
            if (!input.IsCompleted)
            {
                return false;
            }

            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: journal entries must end with a newline.");
        }

        var lineOffset = offset;
        // Account for the delimiter (LF) consumed by TryReadTo.
        offset += lineBuffer.Length + 1;

        using (lineBuffer)
        {
            var line = lineBuffer.AsReadOnlySequence();
            if (!line.IsEmpty && EndsWith(line, CarriageReturn))
            {
                line = line.Slice(0, line.Length - 1);
            }

            if (IsBlankLine(line))
            {
                throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {lineOffset}: blank lines are not valid journal entries.");
            }

            ReadLine(line, lineOffset, resolver, in context);
        }

        return true;
    }

    private static void ReadLine(ReadOnlySequence<byte> line, long offset, IStateResolver resolver, in JournaledStateReplayContext context)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType is not JsonTokenType.StartArray)
            {
                throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must be a JSON array.");
            }

            if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray)
            {
                throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must include a stream id.");
            }

            if (reader.TokenType is not JsonTokenType.Number || !reader.TryGetUInt64(out var streamId))
            {
                throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: element 0 must be an unsigned integer stream id.");
            }

            var stream = new JournalStreamId(streamId);
            var state = resolver.ResolveState(stream);
            var payloadContent = ReadOperationPayload(ref reader, offset);
            using var payloadBuffer = new ArcBufferWriter();
            WriteOperationPayload(line, payloadContent, payloadBuffer);
            var operation = new JournalOperation(JsonJournalExtensions.JournalFormatKey, new JournalReadBuffer(new ArcBufferReader(payloadBuffer), isCompleted: true));
            _ = new JsonOperationReader(operation.Payload);
            state.ApplyOperation(operation, in context);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: invalid JSON journal entry. {exception.Message}", exception);
        }
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

    private readonly struct JsonPayloadContent(long start, long length)
    {
        public long Start { get; } = start;

        public long Length { get; } = length;
    }

    private static JsonPayloadContent ReadOperationPayload(ref Utf8JsonReader reader, long offset)
    {
        if (!reader.Read())
        {
            throw new JsonException("JSON array is incomplete.");
        }

        if (reader.TokenType is JsonTokenType.EndArray)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must include an operation payload.");
        }

        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: element 1 must be a JSON operation payload array.");
        }

        var start = reader.TokenStartIndex;
        var nestedDepth = 0;
        while (true)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                case JsonTokenType.StartObject:
                    nestedDepth++;
                    break;
                case JsonTokenType.EndArray:
                    nestedDepth--;
                    if (nestedDepth == 0)
                    {
                        var length = reader.BytesConsumed - start;
                        if (!reader.Read())
                        {
                            throw new JsonException("JSON array is incomplete.");
                        }

                        if (reader.TokenType is not JsonTokenType.EndArray)
                        {
                            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must contain exactly a stream id and an operation payload.");
                        }

                        EnsureNoTrailingTokens(ref reader);
                        return new(start, length);
                    }
                    break;
                case JsonTokenType.EndObject:
                    if (nestedDepth == 0)
                    {
                        throw new JsonException("JSON array is malformed.");
                    }

                    nestedDepth--;
                    break;
            }

            if (!reader.Read())
            {
                throw new JsonException("JSON array is incomplete.");
            }
        }
    }

    private static void EnsureNoTrailingTokens(ref Utf8JsonReader reader)
    {
        if (reader.Read())
        {
            throw new JsonException("Additional JSON content was found after the journal entry.");
        }
    }

    private static void WriteOperationPayload(ReadOnlySequence<byte> line, JsonPayloadContent payloadContent, ArcBufferWriter buffer)
    {
        buffer.Write(line.Slice(payloadContent.Start, payloadContent.Length));
    }

    private sealed class JsonLinesJournalWriter : JournalWriter
    {
        private readonly ArcBufferWriter _buffer = new();
        private int _activeEntryStart;
        private int _activePayloadStart;

        protected override ArcBuffer GetCommittedBufferCore() => _buffer.PeekSlice(_buffer.Length);

        protected override void ResetCore()
        {
            _activeEntryStart = 0;
            _activePayloadStart = 0;
            _buffer.Reset();
        }

        public override void Dispose() => _buffer.Dispose();

        protected override IBufferWriter<byte> BeginEntryCore(JournalStreamId streamId)
        {
            _activeEntryStart = _buffer.Length;
            WriteJournalEntryPrefix(streamId, _buffer);
            _activePayloadStart = _buffer.Length;
            return _buffer;
        }

        protected override void CommitEntry(JournalStreamId streamId)
        {
            if (_buffer.Length == _activePayloadStart)
            {
                throw new InvalidOperationException("The JSON Lines journal entry has no entry payload.");
            }

            try
            {
                _buffer.Write("]"u8);
                _buffer.Write("\n"u8);
                _activeEntryStart = 0;
                _activePayloadStart = 0;
            }
            catch
            {
                _buffer.Truncate(_activeEntryStart);
                throw;
            }
        }

        protected override void AbortEntry(JournalStreamId streamId)
        {
            _buffer.Truncate(_activeEntryStart);
            _activeEntryStart = 0;
            _activePayloadStart = 0;
        }

        protected override void OnAppendPreservedOperation(JournalStreamId streamId, IPreservedJournalOperation entry)
        {
            if (!string.Equals(entry.FormatKey, JsonJournalExtensions.JournalFormatKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The JSON journal writer cannot append preserved operation of type '{entry.GetType().FullName}'.");
            }

            WriteJournalEntry(streamId, entry.Payload, _buffer);
        }

        private static void WriteJournalEntry(JournalStreamId streamId, ReadOnlyMemory<byte> payload, ArcBufferWriter buffer)
        {
            var entryStart = buffer.Length;
            try
            {
                WriteJournalEntryPrefix(streamId, buffer);
                buffer.Write(payload.Span);
                buffer.Write("]"u8);
                buffer.Write("\n"u8);
            }
            catch
            {
                buffer.Truncate(entryStart);
                throw;
            }
        }

        private static void WriteJournalEntryPrefix(JournalStreamId streamId, ArcBufferWriter buffer)
        {
            var prefix = buffer.GetSpan(22);
            prefix[0] = (byte)'[';
            if (!Utf8Formatter.TryFormat(streamId.Value, prefix[1..], out var streamIdLength))
            {
                throw new InvalidOperationException("Unable to format the JSON Lines journal stream id.");
            }

            var prefixLength = 1 + streamIdLength;
            prefix[prefixLength++] = (byte)',';
            buffer.AdvanceWriter(prefixLength);
        }
    }
}
