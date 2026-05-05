using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Binary codec for durable dictionary log entries, preserving the legacy Orleans binary wire format.
/// </summary>
internal sealed class OrleansBinaryDictionaryOperationCodec<TKey, TValue>(
    ILogValueCodec<TKey> keyCodec,
    ILogValueCodec<TValue> valueCodec) : IDurableDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    private const byte FormatVersion = 0;
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSetPayload(key, value, output));

    private void WriteSetPayload(TKey key, TValue value, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SetCommand);
        keyCodec.Write(key, output);
        valueCodec.Write(value, output);
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteRemovePayload(key, output));

    private void WriteRemovePayload(TKey key, IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, RemoveCommand);
        keyCodec.Write(key, output);
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        var count = CollectionCodecHelpers.GetSnapshotCount(items);
        WriteVersionByte(output);
        VarIntHelper.WriteVarUInt32(output, SnapshotCommand);
        VarIntHelper.WriteVarUInt32(output, (uint)count);
        var written = 0;
        foreach (var (key, value) in items)
        {
            CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            keyCodec.Write(key, output);
            valueCodec.Write(value, output);
            written++;
        }

        CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
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

    private void ApplySet(ReadOnlySequence<byte> remaining, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = keyCodec.Read(remaining, out var consumed);
        remaining = remaining.Slice(consumed);
        var value = valueCodec.Read(remaining, out _);
        consumer.ApplySet(key, value);
    }

    private void ApplyRemove(ReadOnlySequence<byte> remaining, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = keyCodec.Read(remaining, out _);
        consumer.ApplyRemove(key);
    }

    private void ApplySnapshot(ReadOnlySequence<byte> remaining, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new SequenceReader<byte>(remaining);
        var count = CollectionCodecHelpers.ReadSnapshotCount(ref reader);
        remaining = remaining.Slice(reader.Consumed);

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var key = keyCodec.Read(remaining, out var consumed);
            remaining = remaining.Slice(consumed);
            var value = valueCodec.Read(remaining, out consumed);
            remaining = remaining.Slice(consumed);
            consumer.ApplySet(key, value);
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
