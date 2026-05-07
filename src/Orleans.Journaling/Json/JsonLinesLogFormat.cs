using System.Buffers;
using System.Text.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonLinesLogFormat : ILogFormat
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';

    public ILogBatchWriter CreateWriter() => new JsonLinesLogSegmentWriter();

    public void Read(LogReadBuffer input, IStateMachineResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        while (TryReadLine(input, resolver))
        {
        }
    }

    private static bool TryReadLine(LogReadBuffer input, IStateMachineResolver resolver)
    {
        if (input.Length == 0)
        {
            return false;
        }

        if (input.IsNext(Bom))
        {
            throw new InvalidOperationException("Malformed JSON Lines log segment: UTF-8 byte order marks are not supported.");
        }

        if (!input.TryReadTo(out var lineBuffer, LineFeed, advancePastDelimiter: true))
        {
            if (!input.IsCompleted)
            {
                return false;
            }

            throw new InvalidOperationException("Malformed JSON Lines log segment at byte offset 0: log entries must end with a newline.");
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
                throw new InvalidOperationException("Malformed JSON Lines log segment at byte offset 0: blank lines are not valid log entries.");
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
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must be a JSON array.");
            }

            if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray)
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must include a stream id.");
            }

            if (reader.TokenType is not JsonTokenType.Number || !reader.TryGetUInt64(out var streamId))
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: element 0 must be an unsigned integer stream id.");
            }

            var entry = new JsonOperationReader(ref reader);
            var stream = new LogStreamId(streamId);
            var stateMachine = resolver.ResolveStateMachine(stream);
            if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
            {
                var logEntry = ParseLogEntry(line, offset);
                formattedEntryBuffer.AddFormattedEntry(JsonFormattedLogEntry.Create(new JsonOperationEntry(logEntry, offset: 1, logEntry.GetArrayLength() - 1)));
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
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: invalid JSON log entry. {exception.Message}", exception);
        }
    }

    private static JsonElement ParseLogEntry(ReadOnlySequence<byte> line, long offset)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        try
        {
            var result = JsonElement.ParseValue(ref reader);
            if (reader.Read())
            {
                throw new JsonException("Additional JSON content was found after the log entry.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: invalid JSON log entry. {exception.Message}", exception);
        }
    }

    private static void ApplyJsonEntry(LogStreamId streamId, IDurableStateMachine stateMachine, ref JsonOperationReader entry)
    {
        var operationCodec = stateMachine.OperationCodec;
        if (operationCodec is not IJsonLogEntryCodec jsonCodec)
        {
            entry.SkipToEnd();
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The JSON log entry for stream {streamId.Value} resolved to state machine " +
                $"'{stateMachine.GetType().FullName}', but its codec '{codecType}' does not implement IJsonLogEntryCodec.");
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

    private sealed class JsonLinesLogSegmentWriter : LogBatchWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? _payload.Length : 0));

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

            _payload.Reset();
            _buffer.Reset();
        }

        public override void Dispose()
        {
            _payload.Dispose();
            _buffer.Dispose();
        }

        protected override void OnBeginEntry(LogStreamId streamId) => _payload.Reset();

        protected override int GetEntryStart(LogStreamId streamId) => _buffer.Length;

        protected override void AdvancePayload(int count) => _payload.AdvanceWriter(count);

        protected override Memory<byte> GetPayloadMemory(int sizeHint) => _payload.GetMemory(sizeHint);

        protected override Span<byte> GetPayloadSpan(int sizeHint) => _payload.GetSpan(sizeHint);

        protected override void WritePayload(ReadOnlySpan<byte> value) => _payload.Write(value);

        protected override void WritePayload(ReadOnlySequence<byte> value) => _payload.Write(value);

        protected override void CommitEntry(LogStreamId streamId, int entryStart)
        {
            if (_payload.Length == 0)
            {
                throw new InvalidOperationException("The JSON Lines log entry has no entry payload.");
            }

            using var payload = _payload.PeekSlice(_payload.Length);
            WriteLogEntry(streamId, payload.AsReadOnlySequence(), _buffer);
            _payload.Reset();
        }

        protected override void AbortEntry(LogStreamId streamId, int entryStart) => _payload.Reset();

        protected override void OnAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (!OnTryAppendFormattedEntry(streamId, entry))
            {
                throw new InvalidOperationException(
                    $"The JSON log writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
            }
        }

        protected override bool OnTryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (entry is not JsonFormattedLogEntry jsonEntry)
            {
                return false;
            }

            WriteLogEntry(streamId, jsonEntry, _buffer);
            return true;
        }

        private static void WriteLogEntry(LogStreamId streamId, ReadOnlySequence<byte> payload, ArcBufferWriter buffer)
        {
            var entryStart = buffer.Length;
            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException("The JSON Lines log entry payload must be a JSON operation array.");
            }

            try
            {
                using var jsonWriter = new Utf8JsonWriter(buffer);
                WriteLogEntry(jsonWriter, streamId, payloadDocument.RootElement);
                jsonWriter.Flush();
                buffer.Write("\n"u8);
            }
            catch
            {
                buffer.Truncate(entryStart);
                throw;
            }
        }

        private static void WriteLogEntry(LogStreamId streamId, JsonFormattedLogEntry entry, ArcBufferWriter buffer)
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

        private static void WriteLogEntry(Utf8JsonWriter writer, LogStreamId streamId, JsonElement entry)
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

internal abstract class JsonFormattedLogEntry : IFormattedLogEntry
{
    private byte[]? _payload;

    public static JsonFormattedLogEntry Create(JsonOperationEntry payload) => new JsonOperationEntryFormattedLogEntry(payload);

    public static JsonFormattedLogEntry Create<TArg>(TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        return new JsonFormattedLogEntry<TArg>(argument, writeArrayElementsTo);
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
        if (operationCodec is not IJsonLogEntryCodec jsonCodec)
        {
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The JSON log entry resolved to state machine '{stateMachine.GetType().FullName}', " +
                $"but its codec '{codecType}' does not implement IJsonLogEntryCodec.");
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

    private sealed class JsonOperationEntryFormattedLogEntry(JsonOperationEntry entry) : JsonFormattedLogEntry
    {
        public override void WriteTo(Utf8JsonWriter writer) => entry.WriteTo(writer);

        public override void WriteArrayElementsTo(Utf8JsonWriter writer) => entry.WriteArrayElementsTo(writer);
    }
}

internal sealed class JsonFormattedLogEntry<TArg> : JsonFormattedLogEntry
{
    private readonly TArg _argument;
    private readonly Action<Utf8JsonWriter, TArg> _writeArrayElementsTo;

    public JsonFormattedLogEntry(TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
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
