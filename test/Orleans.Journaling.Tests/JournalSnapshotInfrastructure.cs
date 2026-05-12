using System.Buffers;
using System.Text;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Codec-agnostic snapshot formatting helpers used by both the binary and JSONL codec snapshot tests.
/// </summary>
/// <remarks>
/// These helpers intentionally know nothing about specific command discriminators. The OrleansBinary
/// dictionary/list/queue/set/value/state codecs use a varuint command id after the format-version byte,
/// but the TaskCompletionSource codec uses a single status byte. Parsing only the universal framing
/// (entry length prefix, stream id, version byte) keeps the helper applicable to every codec family
/// while still surfacing the per-entry boundaries reviewers need to read snapshot diffs.
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
    /// universal-framing fields the OrleansBinary readers parse (entry body length, stream id, format
    /// version byte) and a 16-byte-per-row hex+ASCII dump of everything after the version byte. If the
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
        var sequence = new ReadOnlySequence<byte>(data);
        var entryIndex = 0;
        var offset = 0;

        while (offset < data.Length)
        {
            // Length prefix.
            if (!TryDecodeVarUInt(sequence.Slice(offset), out var bodyLength, out var prefixSize))
            {
                builder.Append("(unable to parse varuint32 entry length prefix at offset ")
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

            // Stream id.
            if (!TryDecodeVarUInt(sequence.Slice(entryStart, (int)bodyLength), out var streamId, out var streamIdSize))
            {
                builder.Append("[entry ").Append(entryIndex).Append("] length=").Append(bodyLength)
                    .Append(" (unable to parse varuint64 stream id)\n");
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
                    .Append("] length=").Append(bodyLength)
                    .Append(" streamId=").Append(streamId)
                    .Append(" (missing format version byte)\n");
                offset = entryEnd;
                entryIndex++;
                continue;
            }

            var version = data[operationStart];
            var payloadStart = operationStart + 1;
            var payloadLength = operationLength - 1;

            builder.Append("[entry ").Append(entryIndex)
                .Append("] length=").Append(bodyLength)
                .Append(" streamId=").Append(streamId)
                .Append(" version=").Append(version)
                .Append(" payload-bytes=").Append(payloadLength)
                .Append('\n');

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
    private static bool TryDecodeVarUInt(ReadOnlySequence<byte> input, out ulong value, out int bytesRead)
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

/// <summary>
/// <see cref="IStateResolver"/> that returns a single supplied state for a single supplied stream id.
/// </summary>
/// <remarks>
/// The OrleansBinary and JSONL readers dispatch through <see cref="IJournaledState.OperationCodec"/>,
/// then call <c>state is THandler</c> to type-test. Snapshot tests therefore feed a <c>RecordingXxxState</c>
/// instance directly so the same object satisfies both the <see cref="IJournaledState"/> contract and the
/// codec's expected operation handler interface.
/// </remarks>
public sealed class SingleStreamResolver : IStateResolver
{
    private readonly JournalStreamId _streamId;
    private readonly IJournaledState _state;

    public SingleStreamResolver(JournalStreamId streamId, IJournaledState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _streamId = streamId;
        _state = state;
    }

    public IJournaledState ResolveState(JournalStreamId streamId)
    {
        if (streamId != _streamId)
        {
            throw new InvalidOperationException(
                $"SingleStreamResolver only resolves stream {_streamId.Value}, was asked for {streamId.Value}.");
        }

        return _state;
    }
}

/// <summary>
/// Recording <see cref="IJournaledState"/> for dictionary-codec snapshot tests. Implements both the
/// state contract (so the journal readers will dispatch into it) and the dictionary operation handler
/// (so the codec accepts it via <c>state is THandler</c>).
/// </summary>
public sealed class RecordingDictionaryState<TKey, TValue> : IJournaledState, IDictionaryOperationHandler<TKey, TValue>
    where TKey : notnull
{
    private readonly RecordingDictionaryOperationHandler<TKey, TValue> _handler = new();

    public RecordingDictionaryState(IDictionaryOperationCodec<TKey, TValue> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingListState<T> : IJournaledState, IListOperationHandler<T>
{
    private readonly RecordingListOperationHandler<T> _handler = new();

    public RecordingListState(IListOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingQueueState<T> : IJournaledState, IQueueOperationHandler<T>
{
    private readonly RecordingQueueOperationHandler<T> _handler = new();

    public RecordingQueueState(IQueueOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingSetState<T> : IJournaledState, ISetOperationHandler<T>
{
    private readonly RecordingSetOperationHandler<T> _handler = new();

    public RecordingSetState(ISetOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingValueState<T> : IJournaledState, IValueOperationHandler<T>
{
    private readonly RecordingValueOperationHandler<T> _handler = new();

    public RecordingValueState(IValueOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingStateState<T> : IJournaledState, IStateOperationHandler<T>
{
    private readonly RecordingStateOperationHandler<T> _handler = new();

    public RecordingStateState(IStateOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
public sealed class RecordingTcsState<T> : IJournaledState, ITaskCompletionSourceOperationHandler<T>
{
    private readonly RecordingTaskCompletionSourceOperationHandler<T> _handler = new();

    public RecordingTcsState(ITaskCompletionSourceOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        OperationCodec = codec;
    }

    public object OperationCodec { get; }

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
