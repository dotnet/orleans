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
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';
    private const string StreamIdPropertyName = "streamId";
    private const string EntryPropertyName = "entry";

    public ILogSegmentWriter CreateWriter() => new JsonLinesLogSegmentWriter();

    public bool TryRead(ArcBufferReader input, ILogStreamStateMachineResolver resolver, bool isCompleted)
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
            if (!isCompleted)
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

    private static void ReadLine(ReadOnlySequence<byte> line, long offset, ILogStreamStateMachineResolver resolver)
    {
        var reader = new Utf8JsonReader(line);
        JsonLinesLogEntry? logEntry;

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

            logEntry = JsonSerializer.Deserialize(ref reader, JsonLinesLogEntryJsonContext.Default.JsonLinesLogEntry);
            if (reader.Read())
            {
                throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: trailing JSON content after the log entry object.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: invalid JSON log entry.", exception);
        }

        logEntry = ValidateLogEntry(logEntry, offset);
        var streamId = logEntry.StreamId.GetUInt64();
        var stream = new LogStreamId(streamId);
        var stateMachine = resolver.ResolveStateMachine(stream);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new JsonFormattedLogEntry(logEntry.Entry));
            return;
        }

        if (stateMachine is IDurableNothing)
        {
            return;
        }

        ApplyJsonEntry(stream, stateMachine, logEntry.Entry);
    }

    private static JsonLinesLogEntry ValidateLogEntry(JsonLinesLogEntry? logEntry, long offset)
    {
        if (logEntry is null)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: each line must be a JSON object.");
        }

        if (logEntry.ExtensionData is { Count: > 0 })
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: unexpected property '{logEntry.ExtensionData.Keys.First()}'.");
        }

        if (logEntry.DuplicatePropertyName is { } duplicatePropertyName)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: duplicate property '{duplicatePropertyName}'.");
        }

        if (!logEntry.HasStreamId)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing required property '{StreamIdPropertyName}'.");
        }

        if (logEntry.StreamId.ValueKind is not JsonValueKind.Number || !logEntry.StreamId.TryGetUInt64(out _))
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: property '{StreamIdPropertyName}' must be an unsigned integer.");
        }

        if (!logEntry.HasEntry)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: missing required property '{EntryPropertyName}'.");
        }

        if (logEntry.Entry.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Malformed JSON Lines log segment at byte offset {offset}: property '{EntryPropertyName}' must be a JSON object.");
        }

        return logEntry;
    }

    private static void ApplyJsonEntry(LogStreamId streamId, IDurableStateMachine stateMachine, JsonElement entry)
    {
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

        protected override void OnAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (entry is not JsonFormattedLogEntry jsonEntry)
            {
                throw new InvalidOperationException(
                    $"The JSON log writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
            }

            using var logEntry = CreateLogWriter(streamId).BeginEntry();
            logEntry.Writer.Write(jsonEntry.Payload.Span);
            logEntry.Commit();
        }
    }

    private sealed class JsonFormattedLogEntry : IFormattedLogEntry
    {
        public JsonFormattedLogEntry(JsonElement payload)
        {
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonLinesLogEntryJsonContext.Default.JsonElement);
        }

        public ReadOnlyMemory<byte> Payload { get; }
    }
}
