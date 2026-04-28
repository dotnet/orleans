using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable dictionary log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDictionaryEntryCodec<TKey, TValue>(
    ILogDataCodec<TKey> keyCodec,
    ILogDataCodec<TValue> valueCodec) : IDurableDictionaryCodec<TKey, TValue> where TKey : notnull
{
    private const byte FormatVersion = 0;
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetCommand);
        keyCodec.Write(key, output);
        valueCodec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, RemoveCommand);
        keyCodec.Write(key, output);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IEnumerable<KeyValuePair<TKey, TValue>> items, int count, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SnapshotCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)count);
        foreach (var (key, value) in items)
        {
            keyCodec.Write(key, output);
            valueCodec.Write(value, output);
        }
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        ReadVersionByte(ref reader);

        var command = VarIntHelper.ReadVarUInt32(ref reader);
        var remaining = input.Slice(reader.Consumed);

        switch (command)
        {
            case SetCommand:
                ApplySet(remaining, consumer);
                break;
            case RemoveCommand:
                ApplyRemove(remaining, consumer);
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(remaining, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySet(ReadOnlySequence<byte> remaining, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var key = keyCodec.Read(remaining, out var consumed);
        remaining = remaining.Slice(consumed);
        var value = valueCodec.Read(remaining, out _);
        consumer.ApplySet(key, value);
    }

    private void ApplyRemove(ReadOnlySequence<byte> remaining, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var key = keyCodec.Read(remaining, out _);
        consumer.ApplyRemove(key);
    }

    private void ApplySnapshot(ReadOnlySequence<byte> remaining, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var reader = new SequenceReader<byte>(remaining);
        var count = (int)VarIntHelper.ReadVarUInt32(ref reader);
        remaining = remaining.Slice(reader.Consumed);

        consumer.ApplySnapshotStart(count);
        for (var i = 0; i < count; i++)
        {
            var key = keyCodec.Read(remaining, out var consumed);
            remaining = remaining.Slice(consumed);
            var value = valueCodec.Read(remaining, out consumed);
            remaining = remaining.Slice(consumed);
            consumer.ApplySnapshotItem(key, value);
        }
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
