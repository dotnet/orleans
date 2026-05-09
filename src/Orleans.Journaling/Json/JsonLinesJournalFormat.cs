using System.Buffers;
using System.Text.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesJournalFormat : IJournalFormat
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    public string FileExtension => ".jsonl";

    public string? MimeType => "application/jsonl";

    public IJournalBatchWriter CreateWriter() => new JsonLinesJournalBatchWriter();

    public void Read(JournalReadBuffer input, IStateMachineResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        while (TryReadLine(input, resolver))
        {
        }
    }

    private static bool TryReadLine(JournalReadBuffer input, IStateMachineResolver resolver)
    {
        if (input.Length == 0)
        {
            return false;
        }

        if (input.IsNext(Bom))
        {
            throw new InvalidOperationException("Malformed JSON Lines journal segment: UTF-8 byte order marks are not supported.");
        }

        if (!input.TryReadTo(out var lineBuffer, LineFeed, advancePastDelimiter: true))
        {
            if (!input.IsCompleted)
            {
                return false;
            }

            throw new InvalidOperationException("Malformed JSON Lines journal segment at byte offset 0: journal entries must end with a newline.");
        }

        using (lineBuffer)
        {
            var line = lineBuffer.AsReadOnlySequence();
            if (!line.IsEmpty && EndsWith(line, CarriageReturn))
            {
                line = line.Slice(0, line.Length - 1);
            }

            if (IsBlankLine(line))
            {
                throw new InvalidOperationException("Malformed JSON Lines journal segment at byte offset 0: blank lines are not valid journal entries.");
            }

            ReadLine(line, offset: 0, resolver);
        }

        return true;
    }

    private static void ReadLine(ReadOnlySequence<byte> line, long offset, IStateMachineResolver resolver)
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

            var entry = new JsonOperationReader(ref reader);
            var stream = new JournalStreamId(streamId);
            var stateMachine = resolver.ResolveStateMachine(stream);
            if (stateMachine is IFormattedJournalEntryBuffer formattedEntryBuffer)
            {
                var journalEntry = ParseJournalEntry(line, offset);
                formattedEntryBuffer.AddFormattedEntry(JsonFormattedJournalEntry.Create(new JsonOperationEntry(journalEntry, offset: 1, journalEntry.GetArrayLength() - 1)));
                return;
            }

            if (stateMachine is IDurableNothing)
            {
                entry.SkipToEnd();
                return;
            }

            ApplyJsonEntry(stream, stateMachine, ref entry);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: invalid JSON journal entry. {exception.Message}", exception);
        }
    }

    private static JsonElement ParseJournalEntry(ReadOnlySequence<byte> line, long offset)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        try
        {
            var result = JsonElement.ParseValue(ref reader);
            if (reader.Read())
            {
                throw new JsonException("Additional JSON content was found after the journal entry.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines journal segment at byte offset {offset}: invalid JSON journal entry. {exception.Message}", exception);
        }
    }

    private static void ApplyJsonEntry(JournalStreamId streamId, IDurableStateMachine stateMachine, ref JsonOperationReader entry)
    {
        var operationCodec = stateMachine.OperationCodec;
        if (operationCodec is not IJsonJournalEntryCodec jsonCodec)
        {
            entry.SkipToEnd();
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The JSON journal entry for stream {streamId.Value} resolved to state machine " +
                $"'{stateMachine.GetType().FullName}', but its codec '{codecType}' does not implement IJsonJournalEntryCodec.");
        }

        jsonCodec.Apply(ref entry, stateMachine);
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

    private sealed class JsonLinesJournalBatchWriter : JournalBatchWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? _payload.Length : 0));

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The JSON Lines journal segment has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The JSON Lines journal segment cannot be reset while an entry is active.");
            }

            _payload.Reset();
            _buffer.Reset();
        }

        public override void Dispose()
        {
            _payload.Dispose();
            _buffer.Dispose();
        }

        protected override void OnBeginEntry(JournalStreamId streamId) => _payload.Reset();

        protected override int GetEntryStart(JournalStreamId streamId) => _buffer.Length;

        protected override void AdvancePayload(int count) => _payload.AdvanceWriter(count);

        protected override Memory<byte> GetPayloadMemory(int sizeHint) => _payload.GetMemory(sizeHint);

        protected override Span<byte> GetPayloadSpan(int sizeHint) => _payload.GetSpan(sizeHint);

        protected override void WritePayload(ReadOnlySpan<byte> value) => _payload.Write(value);

        protected override void WritePayload(ReadOnlySequence<byte> value) => _payload.Write(value);

        protected override void CommitEntry(JournalStreamId streamId, int entryStart)
        {
            if (_payload.Length == 0)
            {
                throw new InvalidOperationException("The JSON Lines journal entry has no entry payload.");
            }

            using var payload = _payload.PeekSlice(_payload.Length);
            WriteJournalEntry(streamId, payload.AsReadOnlySequence(), _buffer);
            _payload.Reset();
        }

        protected override void AbortEntry(JournalStreamId streamId, int entryStart) => _payload.Reset();

        protected override void OnAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
        {
            if (!OnTryAppendFormattedEntry(streamId, entry))
            {
                throw new InvalidOperationException(
                    $"The JSON journal writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
            }
        }

        protected override bool OnTryAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
        {
            if (entry is not JsonFormattedJournalEntry jsonEntry)
            {
                return false;
            }

            WriteJournalEntry(streamId, jsonEntry, _buffer);
            return true;
        }

        private static void WriteJournalEntry(JournalStreamId streamId, ReadOnlySequence<byte> payload, ArcBufferWriter buffer)
        {
            var entryStart = buffer.Length;
            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException("The JSON Lines journal entry payload must be a JSON operation array.");
            }

            try
            {
                using var jsonWriter = new Utf8JsonWriter(buffer);
                WriteJournalEntry(jsonWriter, streamId, payloadDocument.RootElement);
                jsonWriter.Flush();
                buffer.Write("\n"u8);
            }
            catch
            {
                buffer.Truncate(entryStart);
                throw;
            }
        }

        private static void WriteJournalEntry(JournalStreamId streamId, JsonFormattedJournalEntry entry, ArcBufferWriter buffer)
        {
            var entryStart = buffer.Length;
            try
            {
                using var jsonWriter = new Utf8JsonWriter(buffer);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteNumberValue(streamId.Value);
                entry.WriteArrayElementsTo(jsonWriter);
                jsonWriter.WriteEndArray();
                jsonWriter.Flush();
                buffer.Write("\n"u8);
            }
            catch
            {
                buffer.Truncate(entryStart);
                throw;
            }
        }

        private static void WriteJournalEntry(Utf8JsonWriter writer, JournalStreamId streamId, JsonElement entry)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(streamId.Value);
            foreach (var element in entry.EnumerateArray())
            {
                element.WriteTo(writer);
            }

            writer.WriteEndArray();
        }
    }
}

internal abstract class JsonFormattedJournalEntry : IFormattedJournalEntry
{
    private byte[]? _payload;

    public static JsonFormattedJournalEntry Create(JsonOperationEntry payload) => new JsonOperationEntryFormattedJournalEntry(payload);

    public static JsonFormattedJournalEntry Create<TArg>(TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        return new JsonFormattedJournalEntry<TArg>(argument, writeArrayElementsTo);
    }

    public ReadOnlyMemory<byte> Payload => _payload ??= SerializePayload();

    public abstract void WriteTo(Utf8JsonWriter writer);

    public abstract void WriteArrayElementsTo(Utf8JsonWriter writer);

    public void Apply(IDurableStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        if (stateMachine is IDurableNothing)
        {
            return;
        }

        var operationCodec = stateMachine.OperationCodec;
        if (operationCodec is not IJsonJournalEntryCodec jsonCodec)
        {
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The JSON journal entry resolved to state machine '{stateMachine.GetType().FullName}', " +
                $"but its codec '{codecType}' does not implement IJsonJournalEntryCodec.");
        }

        var reader = new JsonOperationReader(new ReadOnlySequence<byte>(Payload));
        jsonCodec.Apply(ref reader, stateMachine);
    }

    private byte[] SerializePayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteTo(writer);
        writer.Flush();
        return buffer.WrittenMemory.ToArray();
    }

    private sealed class JsonOperationEntryFormattedJournalEntry(JsonOperationEntry entry) : JsonFormattedJournalEntry
    {
        public override void WriteTo(Utf8JsonWriter writer) => entry.WriteTo(writer);

        public override void WriteArrayElementsTo(Utf8JsonWriter writer) => entry.WriteArrayElementsTo(writer);
    }
}

internal sealed class JsonFormattedJournalEntry<TArg> : JsonFormattedJournalEntry
{
    private readonly TArg _argument;
    private readonly Action<Utf8JsonWriter, TArg> _writeArrayElementsTo;

    public JsonFormattedJournalEntry(TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        _argument = argument;
        _writeArrayElementsTo = writeArrayElementsTo;
    }

    public override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        _writeArrayElementsTo(writer, _argument);
        writer.WriteEndArray();
    }

    public override void WriteArrayElementsTo(Utf8JsonWriter writer) => _writeArrayElementsTo(writer, _argument);
}
