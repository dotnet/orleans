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

    public bool TryRead(LogReadBuffer input, IStateMachineResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

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
        JsonElement logEntry;

        try
        {
            using var document = JsonDocument.Parse(line);
            logEntry = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: invalid JSON log entry. {exception.Message}", exception);
        }

        if (logEntry.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must be a JSON array.");
        }

        var length = logEntry.GetArrayLength();
        if (length == 0)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must include a stream id.");
        }

        var streamElement = logEntry[0];
        if (streamElement.ValueKind is not JsonValueKind.Number || !streamElement.TryGetUInt64(out var streamId))
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: element 0 must be an unsigned integer stream id.");
        }

        if (length == 1)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must include an operation command.");
        }

        if (logEntry[1].ValueKind is not JsonValueKind.String)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: element 1 must be an operation command string.");
        }

        var entry = CreateOperationEntry(logEntry);
        var stream = new LogStreamId(streamId);
        var stateMachine = resolver.ResolveStateMachine(stream);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(JsonFormattedLogEntry.Create(entry));
            return;
        }

        if (stateMachine is IDurableNothing)
        {
            return;
        }

        ApplyJsonEntry(stream, stateMachine, entry);
    }

    private static void ApplyJsonEntry(LogStreamId streamId, IDurableStateMachine stateMachine, JsonElement entry)
    {
        if (entry.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment for stream {streamId.Value}: operation entry must be a JSON array.");
        }

        var operationCodec = stateMachine.OperationCodec;
        if (operationCodec is not IJsonLogEntryCodec jsonCodec)
        {
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The JSON log entry for stream {streamId.Value} resolved to state machine " +
                $"'{stateMachine.GetType().FullName}', but its codec '{codecType}' does not implement IJsonLogEntryCodec.");
        }

        jsonCodec.Apply(entry, stateMachine);
    }

    private static JsonElement CreateOperationEntry(JsonElement logEntry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            var length = logEntry.GetArrayLength();
            for (var i = 1; i < length; i++)
            {
                logEntry[i].WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
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
            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException("The JSON Lines log entry payload must be a JSON operation array.");
            }

            using var jsonWriter = new Utf8JsonWriter(buffer);
            WriteLogEntry(jsonWriter, streamId, payloadDocument.RootElement);
            jsonWriter.Flush();
            buffer.Write("\n"u8);
        }

        private static void WriteLogEntry(LogStreamId streamId, JsonFormattedLogEntry entry, ArcBufferWriter buffer)
        {
            using var jsonWriter = new Utf8JsonWriter(buffer);
            jsonWriter.WriteStartArray();
            jsonWriter.WriteNumberValue(streamId.Value);
            entry.WriteArrayElementsTo(jsonWriter);
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
            buffer.Write("\n"u8);
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

    public static JsonFormattedLogEntry Create(JsonElement payload) => new JsonElementFormattedLogEntry(payload);

    public static JsonFormattedLogEntry Create<TArg>(TArg argument, Action<Utf8JsonWriter, TArg> writeArrayElementsTo)
    {
        ArgumentNullException.ThrowIfNull(writeArrayElementsTo);
        return new JsonFormattedLogEntry<TArg>(argument, writeArrayElementsTo);
    }

    public ReadOnlyMemory<byte> Payload => _payload ??= SerializePayload();

    public abstract void WriteTo(Utf8JsonWriter writer);

    public abstract void WriteArrayElementsTo(Utf8JsonWriter writer);

    private byte[] SerializePayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteTo(writer);
        writer.Flush();
        return buffer.WrittenMemory.ToArray();
    }

    private sealed class JsonElementFormattedLogEntry : JsonFormattedLogEntry
    {
        private readonly JsonElement _element;

        public JsonElementFormattedLogEntry(JsonElement payload)
        {
            _element = payload.Clone();
        }

        public override void WriteTo(Utf8JsonWriter writer) => _element.WriteTo(writer);

        public override void WriteArrayElementsTo(Utf8JsonWriter writer)
        {
            if (_element.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidOperationException("The JSON formatted log entry payload must be a JSON operation array.");
            }

            foreach (var element in _element.EnumerateArray())
            {
                element.WriteTo(writer);
            }
        }
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
