using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for <see cref="DurableDictionaryEntry{TKey, TValue}"/> log entries,
/// preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDictionaryEntryCodec<TKey, TValue>(
    ILogDataCodec<TKey> keyCodec,
    ILogDataCodec<TValue> valueCodec) : ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>
{
    private const byte FormatVersion = 0;
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void Write(DurableDictionaryEntry<TKey, TValue> entry, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);

        switch (entry)
        {
            case DictionarySetEntry<TKey, TValue>(var key, var value):
                VarIntHelper.WriteVarUInt32(output, SetCommand);
                keyCodec.Write(key, output);
                valueCodec.Write(value, output);
                break;
            case DictionaryRemoveEntry<TKey, TValue>(var key):
                VarIntHelper.WriteVarUInt32(output, RemoveCommand);
                keyCodec.Write(key, output);
                break;
            case DictionaryClearEntry<TKey, TValue>:
                VarIntHelper.WriteVarUInt32(output, ClearCommand);
                break;
            case DictionarySnapshotEntry<TKey, TValue>(var items):
                VarIntHelper.WriteVarUInt32(output, SnapshotCommand);
                VarIntHelper.WriteVarUInt32(output, (uint)items.Count);
                foreach (var (key, value) in items)
                {
                    keyCodec.Write(key, output);
                    valueCodec.Write(value, output);
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }
    }

    /// <inheritdoc/>
    public DurableDictionaryEntry<TKey, TValue> Read(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        return command switch
        {
            SetCommand => ReadSet(remaining),
            RemoveCommand => ReadRemove(remaining),
            ClearCommand => new DictionaryClearEntry<TKey, TValue>(),
            SnapshotCommand => ReadSnapshot(remaining),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DictionarySetEntry<TKey, TValue> ReadSet(ReadOnlySequence<byte> remaining)
    {
        var key = keyCodec.Read(remaining, out var consumed);
        remaining = remaining.Slice(consumed);
        var value = valueCodec.Read(remaining, out _);
        return new DictionarySetEntry<TKey, TValue>(key, value);
    }

    private DictionaryRemoveEntry<TKey, TValue> ReadRemove(ReadOnlySequence<byte> remaining)
    {
        var key = keyCodec.Read(remaining, out _);
        return new DictionaryRemoveEntry<TKey, TValue>(key);
    }

    private DictionarySnapshotEntry<TKey, TValue> ReadSnapshot(ReadOnlySequence<byte> remaining)
    {
        var reader = new SequenceReader<byte>(remaining);
        var count = (int)VarIntHelper.ReadVarUInt32(ref reader);
        remaining = remaining.Slice(reader.Consumed);

        var items = new List<KeyValuePair<TKey, TValue>>(count);
        for (var i = 0; i < count; i++)
        {
            var key = keyCodec.Read(remaining, out var consumed);
            remaining = remaining.Slice(consumed);
            var value = valueCodec.Read(remaining, out consumed);
            remaining = remaining.Slice(consumed);
            items.Add(new KeyValuePair<TKey, TValue>(key, value));
        }

        return new DictionarySnapshotEntry<TKey, TValue>(items);
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

    private static void ReadVersionByte(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var version) || version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version: {version}");
        }
    }
}
