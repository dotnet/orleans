using System.Buffers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Codec-agnostic snapshot formatting helpers used by both the binary and JSONL codec snapshot tests.
/// </summary>
/// <remarks>
/// These helpers intentionally know nothing about specific command discriminators. The OrleansBinary
/// dictionary/list/queue/set/value/state codecs use a varuint command id, but the TaskCompletionSource
/// codec uses a single status byte. Parsing only the universal framing (entry length prefix and stream id)
/// keeps the helper applicable to every codec family while still surfacing the per-entry boundaries
/// reviewers need to read snapshot diffs.
/// </remarks>
public static class JournalSnapshotFormatting
{
    /// <summary>
    /// Returns an uppercase, no-separator hexadecimal representation of <paramref name="bytes"/>.
    /// </summary>
    /// <remarks>
    /// This is the byte-level baseline used as the first line of binary snapshot files: any wire-format
    /// drift fails CI by changing this single line.
    /// </remarks>
    public static string HexBaseline(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>
    /// Renders an OrleansBinary journal segment as a snapshot-friendly multi-line view.
    /// </summary>
    /// <remarks>
    /// The output begins with a single <c>HEX: …</c> line (the byte-level baseline) followed by a
    /// <c>DISASSEMBLY:</c> section. For each entry the disassembly emits a header line with the
    /// universal-framing fields the OrleansBinary readers parse (entry frame version, entry body length,
    /// and stream id) and a 16-byte-per-row hex+ASCII dump of the command payload. If the
    /// segment is malformed the disassembly falls back to a raw dump of the unparsed tail; this lets the
    /// helper be used safely even for adversarial inputs without throwing during snapshot generation.
    /// </remarks>
    public static string FormatBinary(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder();
        builder.Append("HEX: ").Append(HexBaseline(bytes)).Append('\n');
        builder.Append("DISASSEMBLY:\n");

        if (bytes.Length == 0)
        {
            builder.Append("(empty)\n");
            return builder.ToString();
        }

        // Copy to an array so we can re-use the byte[] from inside the loop without being constrained by
        // span semantics across method boundaries (StringBuilder calls, hex/ASCII dump helper).
        var data = bytes.ToArray();
        using var bufferWriter = new ArcBufferWriter();
        bufferWriter.Write(data);
        using var buffer = bufferWriter.PeekSlice(bufferWriter.Length);
        var entryIndex = 0;
        var offset = 0;

        while (offset < data.Length)
        {
            byte framingVersion;
            uint bodyLength;
            int prefixSize;
            var hasFrameHeader = false;
            try
            {
                hasFrameHeader = OrleansBinaryJournalReader.TryReadVersionAndLength(
                    buffer.UnsafeSlice(offset, data.Length - offset),
                    out framingVersion,
                    out bodyLength,
                    out prefixSize);
            }
            catch
            {
                framingVersion = OrleansBinaryJournalReader.LegacyFramingVersion;
                bodyLength = 0;
                prefixSize = 0;
            }

            if (!hasFrameHeader)
            {
                builder.Append("(unable to parse entry version/length prefix at offset ")
                    .Append(offset)
                    .Append(")\n");
                AppendHexAsciiDump(builder, data.AsSpan(offset));
                return builder.ToString();
            }

            var entryStart = offset + prefixSize;
            var entryEnd = entryStart + (int)bodyLength;
            if (bodyLength == 0 || entryEnd > data.Length)
            {
                builder.Append("(malformed entry at offset ")
                    .Append(offset)
                    .Append(": body length=")
                    .Append(bodyLength)
                    .Append(", remaining=")
                    .Append(data.Length - entryStart)
                    .Append(")\n");
                AppendHexAsciiDump(builder, data.AsSpan(offset));
                return builder.ToString();
            }

            ulong streamId;
            int streamIdSize;
            if (framingVersion == OrleansBinaryJournalReader.FramingVersion)
            {
                if (bodyLength < sizeof(uint))
                {
                    builder.Append("[entry ").Append(entryIndex)
                        .Append("] length=").Append(bodyLength)
                        .Append(" (missing fixed-width stream id)\n");
                    AppendHexAsciiDump(builder, data.AsSpan(entryStart, (int)bodyLength));
                    offset = entryEnd;
                    entryIndex++;
                    continue;
                }

                streamId = OrleansBinaryJournalReader.ReadUInt32LittleEndian(buffer.UnsafeSlice(entryStart, sizeof(uint)));
                streamIdSize = sizeof(uint);
            }
            else if (!TryDecodeVarUInt(buffer.UnsafeSlice(entryStart, (int)bodyLength), out streamId, out streamIdSize))
            {
                builder.Append("[entry ").Append(entryIndex).Append("] length=").Append(bodyLength)
                    .Append(" (unable to parse varuint32 stream id)\n");
                AppendHexAsciiDump(builder, data.AsSpan(entryStart, (int)bodyLength));
                offset = entryEnd;
                entryIndex++;
                continue;
            }

            var operationStart = entryStart + streamIdSize;
            var operationLength = entryEnd - operationStart;
            if (operationLength <= 0)
            {
                builder.Append("[entry ").Append(entryIndex)
                    .Append("] frame-version=").Append(framingVersion)
                    .Append(" length=").Append(bodyLength)
                    .Append(" streamId=").Append(streamId)
                    .Append(" (missing command payload)\n");
                offset = entryEnd;
                entryIndex++;
                continue;
            }

            builder.Append("[entry ").Append(entryIndex)
                .Append("] frame-version=").Append(framingVersion)
                .Append(" length=").Append(bodyLength)
                .Append(" streamId=").Append(streamId)
                .Append(" payload-bytes=");

            int payloadStart;
            int payloadLength;
            if (framingVersion == OrleansBinaryJournalReader.LegacyFramingVersion)
            {
                var legacyCommandVersion = data[operationStart];
                payloadStart = operationStart + 1;
                payloadLength = operationLength - 1;
                builder.Append(payloadLength)
                    .Append(" legacy-command-version=").Append(legacyCommandVersion)
                    .Append('\n');
            }
            else
            {
                payloadStart = operationStart;
                payloadLength = operationLength;
                builder.Append(payloadLength).Append('\n');
            }

            AppendHexAsciiDump(builder, data.AsSpan(payloadStart, payloadLength));

            offset = entryEnd;
            entryIndex++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes one Orleans-format varint (variable-length unsigned integer). Returns <c>false</c> when
    /// the input does not contain a complete varint.
    /// </summary>
    private static bool TryDecodeVarUInt(ArcBuffer input, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        if (input.Length == 0)
        {
            return false;
        }

        try
        {
            var reader = Reader.Create(input, session: null!);
            value = reader.ReadVarUInt64();
            bytesRead = (int)reader.Position;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Appends a canonical <c>OFFSET  16-byte hex  | ASCII</c> dump of <paramref name="bytes"/> to
    /// <paramref name="builder"/>. Output uses literal <c>\n</c> so the produced text is identical on
    /// Windows and Linux CI without depending on <see cref="Environment.NewLine"/>.
    /// </summary>
    private static void AppendHexAsciiDump(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            builder.Append("    (no payload)\n");
            return;
        }

        const int Width = 16;
        for (var i = 0; i < bytes.Length; i += Width)
        {
            var rowLength = Math.Min(Width, bytes.Length - i);
            builder.Append(i.ToString("X4"));
            builder.Append("  ");

            for (var j = 0; j < Width; j++)
            {
                if (j == Width / 2)
                {
                    builder.Append(' ');
                }

                if (j < rowLength)
                {
                    builder.Append(bytes[i + j].ToString("X2"));
                    builder.Append(' ');
                }
                else
                {
                    builder.Append("   ");
                }
            }

            builder.Append("  |");
            for (var j = 0; j < rowLength; j++)
            {
                var b = bytes[i + j];
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append("|\n");
        }
    }
}

public static class JournalTestReplayContext
{
    public static JournalReplayContext Create(string journalFormatKey, params (JournalStreamId StreamId, IJournaledState State)[] states)
    {
        var manager = CreateManager(journalFormatKey);
        foreach (var (streamId, state) in states)
        {
            manager.BindStateForReplay(streamId, state);
        }

        return new(manager);
    }

    private static JournaledStateManager CreateManager(string journalFormatKey)
    {
        journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IJournalFormat>(journalFormatKey, new TestJournalFormat(journalFormatKey));
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, uint>>(journalFormatKey, new UnsupportedDictionaryCommandCodec<uint>());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, DateTime>>(journalFormatKey, new UnsupportedDictionaryCommandCodec<DateTime>());

        var serviceProvider = services.BuildServiceProvider();
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }),
            TimeProvider.System,
            new NullJournalStorage(),
            serviceProvider);

        return new JournaledStateManager(shared);
    }

    private sealed class TestJournalFormat(string journalFormatKey) : IJournalFormat
    {
        public string FormatKey { get; } = journalFormatKey;

        public string? MimeType => null;

        public JournalBufferWriter CreateWriter() => new OrleansBinaryJournalBufferWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class UnsupportedDictionaryCommandCodec<TValue> : IDurableDictionaryCommandCodec<string, TValue>
    {
        public void WriteSet(string key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(string key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class NullJournalStorage : IJournalStorage
    {
        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => default;

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }
}

/// <summary>
/// Recording <see cref="IJournaledState"/> for dictionary-codec snapshot tests. Implements both the
/// state contract (so the journal readers will dispatch into it) and the dictionary operation handler
/// (so the codec accepts it via <c>state is THandler</c>).
/// </summary>
public sealed class RecordingDictionaryState<TKey, TValue> : IJournaledState, IDurableDictionaryCommandHandler<TKey, TValue>
    where TKey : notnull
{
    private readonly IDurableDictionaryCommandCodec<TKey, TValue> _codec;
    private readonly RecordingDictionaryCommandHandler<TKey, TValue> _handler = new();

    public RecordingDictionaryState(IDurableDictionaryCommandCodec<TKey, TValue> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public IReadOnlyList<KeyValuePair<TKey, TValue>> SnapshotItems => _handler.SnapshotItems;

    public void ApplySet(TKey key, TValue value) => _handler.ApplySet(key, value);

    public void ApplyRemove(TKey key) => _handler.ApplyRemove(key);

    public void ApplyClear() => _handler.ApplyClear();

    public void Reset(int capacityHint) => _handler.Reset(capacityHint);

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for list-codec snapshot tests.</summary>
public sealed class RecordingListState<T> : IJournaledState, IDurableListCommandHandler<T>
{
    private readonly IDurableListCommandCodec<T> _codec;
    private readonly RecordingListCommandHandler<T> _handler = new();

    public RecordingListState(IDurableListCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public void ApplyAdd(T item) => _handler.ApplyAdd(item);

    public void ApplySet(int index, T item) => _handler.ApplySet(index, item);

    public void ApplyInsert(int index, T item) => _handler.ApplyInsert(index, item);

    public void ApplyRemoveAt(int index) => _handler.ApplyRemoveAt(index);

    public void ApplyClear() => _handler.ApplyClear();

    public void Reset(int capacityHint) => _handler.Reset(capacityHint);

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for queue-codec snapshot tests.</summary>
public sealed class RecordingQueueState<T> : IJournaledState, IDurableQueueCommandHandler<T>
{
    private readonly IDurableQueueCommandCodec<T> _codec;
    private readonly RecordingQueueCommandHandler<T> _handler = new();

    public RecordingQueueState(IDurableQueueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public void ApplyEnqueue(T item) => _handler.ApplyEnqueue(item);

    public void ApplyDequeue() => _handler.ApplyDequeue();

    public void ApplyClear() => _handler.ApplyClear();

    public void Reset(int capacityHint) => _handler.Reset(capacityHint);

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for set-codec snapshot tests.</summary>
public sealed class RecordingSetState<T> : IJournaledState, IDurableSetCommandHandler<T>
{
    private readonly IDurableSetCommandCodec<T> _codec;
    private readonly RecordingSetCommandHandler<T> _handler = new();

    public RecordingSetState(IDurableSetCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public void ApplyAdd(T item) => _handler.ApplyAdd(item);

    public void ApplyRemove(T item) => _handler.ApplyRemove(item);

    public void ApplyClear() => _handler.ApplyClear();

    public void Reset(int capacityHint) => _handler.Reset(capacityHint);

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for value-codec snapshot tests.</summary>
public sealed class RecordingValueState<T> : IJournaledState, IDurableValueCommandHandler<T>
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private readonly RecordingValueCommandHandler<T> _handler = new();

    public RecordingValueState(IDurableValueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public T? Value => _handler.Value;

    public bool ValueWasSet { get; private set; }

    public void ApplySet(T value)
    {
        _handler.ApplySet(value);
        ValueWasSet = true;
    }

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for state-codec snapshot tests.</summary>
public sealed class RecordingStateState<T> : IJournaledState, IPersistentStateCommandHandler<T>
{
    private readonly IPersistentStateCommandCodec<T> _codec;
    private readonly RecordingPersistentStateCommandHandler<T> _handler = new();

    public RecordingStateState(IPersistentStateCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public T? State => _handler.State;

    public ulong Version => _handler.Version;

    public void ApplySet(T state, ulong version) => _handler.ApplySet(state, version);

    public void ApplyClear() => _handler.ApplyClear();

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}

/// <summary>Recording state for task-completion-source codec snapshot tests.</summary>
public sealed class RecordingTcsState<T> : IJournaledState, IDurableTaskCompletionSourceCommandHandler<T>
{
    private readonly IDurableTaskCompletionSourceCommandCodec<T> _codec;
    private readonly RecordingTaskCompletionSourceCommandHandler<T> _handler = new();

    public RecordingTcsState(IDurableTaskCompletionSourceCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public IReadOnlyList<string> Commands => _handler.Commands;

    public void ApplyPending() => _handler.ApplyPending();

    public void ApplyCompleted(T value) => _handler.ApplyCompleted(value);

    public void ApplyFaulted(Exception exception) => _handler.ApplyFaulted(exception);

    public void ApplyCanceled() => _handler.ApplyCanceled();

    public void Reset(JournalStreamWriter writer) { }

    public void AppendEntries(JournalStreamWriter writer) { }

    public void AppendSnapshot(JournalStreamWriter writer) { }

    public IJournaledState DeepCopy() => throw new NotSupportedException();
}
