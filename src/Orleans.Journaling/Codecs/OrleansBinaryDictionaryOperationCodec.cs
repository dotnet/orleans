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
        var reader = new OrleansBinaryOperationReader(input);
        var command = reader.ReadCommand();

        switch (command)
        {
            case SetCommand:
                ApplySet(ref reader, consumer);
                break;
            case RemoveCommand:
                ApplyRemove(ref reader, consumer);
                break;
            case ClearCommand:
                reader.EnsureEnd();
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ApplySnapshot(ref reader, consumer);
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }

    private void ApplySet(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = reader.ReadValue("key", keyCodec);
        var value = reader.ReadValue("value", valueCodec);
        reader.EnsureEnd();
        consumer.ApplySet(key, value);
    }

    private void ApplyRemove(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var key = reader.ReadValue("key", keyCodec);
        reader.EnsureEnd();
        consumer.ApplyRemove(key);
    }

    private void ApplySnapshot(ref OrleansBinaryOperationReader reader, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var count = reader.ReadSnapshotCount();

        consumer.Reset(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadValue("key", keyCodec);
            var value = reader.ReadValue("value", valueCodec);
            consumer.ApplySet(key, value);
        }

        reader.EnsureEnd();
    }

    private static void WriteVersionByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = FormatVersion;
        output.Advance(1);
    }

}
