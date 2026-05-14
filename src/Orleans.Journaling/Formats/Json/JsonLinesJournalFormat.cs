using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesJournalFormat : IJournalFormat
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    public string FormatKey => JsonJournalExtensions.JournalFormatKey;

    public string? MimeType => "application/jsonl";

    public JournalBufferWriter CreateWriter() => new JsonLinesJournalBufferWriter();

    public void Replay(JournalBufferReader input, JournalReplayContext context)
    {
        var offset = 0L;
        while (TryReadLine(input, context, ref offset))
        {
        }
    }

    private static bool TryReadLine(JournalBufferReader input, JournalReplayContext context, ref long offset)
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

            ReadLine(line, lineOffset, context);
        }

        return true;
    }

    private static void ReadLine(ReadOnlySequence<byte> line, long offset, JournalReplayContext context)
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

            if (reader.TokenType is not JsonTokenType.Number || !reader.TryGetUInt32(out var streamId))
            {
                throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: element 0 must be an unsigned 32-bit integer stream id.");
            }

            var stream = new JournalStreamId(streamId);
            var state = context.ResolveState(stream);
            var payloadContent = ReadEntryPayload(ref reader, offset);
            using var payloadBuffer = new ArcBufferWriter();
            WriteEntryPayload(line, payloadContent, payloadBuffer);
            var entry = new JournalEntry(JsonJournalExtensions.JournalFormatKey, new JournalBufferReader(payloadBuffer.Reader, isCompleted: true));
            using var commandReader = new JsonCommandReader(entry.Reader);
            state.ReplayEntry(entry, context);
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

    private static JsonPayloadContent ReadEntryPayload(ref Utf8JsonReader reader, long offset)
    {
        if (!reader.Read())
        {
            throw new JsonException("JSON array is incomplete.");
        }

        if (reader.TokenType is JsonTokenType.EndArray)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must include an entry payload.");
        }

        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: element 1 must be a JSON entry payload array.");
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
                            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: each line must contain exactly a stream id and an entry payload.");
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

    private static void WriteEntryPayload(ReadOnlySequence<byte> line, JsonPayloadContent payloadContent, ArcBufferWriter buffer)
    {
        buffer.Write(line.Slice(payloadContent.Start, payloadContent.Length));
    }

    private sealed class JsonLinesJournalBufferWriter : JournalBufferWriter
    {
        protected override void StartEntry(JournalStreamId streamId)
        {
            WriteJournalEntryPrefix(streamId, Output);
        }

        protected override void FinishEntry(JournalStreamId streamId)
        {
            if (GetEntryByte(ActiveEntryLength - 1) == (byte)',')
            {
                throw new InvalidOperationException("The JSON Lines journal entry has no entry payload.");
            }

            Output.Write("]\n"u8);
        }

        protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
        {
            if (!string.Equals(entry.FormatKey, JsonJournalExtensions.JournalFormatKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The JSON journal buffer writer cannot append preserved entry of type '{entry.GetType().FullName}'.");
            }

            var payload = entry.Payload;
            if (payload.IsEmpty)
            {
                throw new InvalidOperationException("The JSON Lines journal entry has no entry payload.");
            }

            WriteJournalEntryPrefix(streamId, Output);
            Output.Write(payload.Span);
            Output.Write("]\n"u8);
        }

        private static void WriteJournalEntryPrefix(JournalStreamId streamId, IBufferWriter<byte> output)
        {
            var prefix = output.GetSpan(12);
            prefix[0] = (byte)'[';
            if (!Utf8Formatter.TryFormat(streamId.Value, prefix[1..], out var streamIdLength))
            {
                throw new InvalidOperationException("Unable to format the JSON Lines journal stream id.");
            }

            var prefixLength = 1 + streamIdLength;
            prefix[prefixLength++] = (byte)',';
            output.Advance(prefixLength);
        }
    }
}
