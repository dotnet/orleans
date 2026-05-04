using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable dictionary log entries.
/// </summary>
public sealed class ProtobufDictionaryOperationCodec<TKey, TValue>(
    ProtobufValueConverter<TKey> keyConverter,
    ProtobufValueConverter<TValue> valueConverter) : IDurableDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, LogStreamWriter writer) =>
        ProtobufOperationCodecWriter.Write(writer, output => WriteSetPayload(key, value, output));

    private void WriteSetPayload(TKey key, TValue value, IBufferWriter<byte> output)
    {
        var operation = new ProtobufDictionaryOperation();
        operation.Command.Add(SetCommand);
        operation.Key.Add(keyConverter.ToByteString(key));
        operation.Value.Add(valueConverter.ToByteString(value));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, LogStreamWriter writer) =>
        ProtobufOperationCodecWriter.Write(writer, output => WriteRemovePayload(key, output));

    private void WriteRemovePayload(TKey key, IBufferWriter<byte> output)
    {
        var operation = new ProtobufDictionaryOperation();
        operation.Command.Add(RemoveCommand);
        operation.Key.Add(keyConverter.ToByteString(key));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) =>
        ProtobufOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        var operation = new ProtobufDictionaryOperation();
        operation.Command.Add(ClearCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer) =>
        ProtobufOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        var count = ProtobufGeneratedCodecHelpers.GetSnapshotCount(items);
        var operation = new ProtobufDictionaryOperation();
        operation.Command.Add(SnapshotCommand);
        operation.Count.Add((uint)count);
        var written = 0;
        foreach (var (key, value) in items)
        {
            ProtobufGeneratedCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            operation.Key.Add(keyConverter.ToByteString(key));
            operation.Value.Add(valueConverter.ToByteString(value));
            written++;
        }

        ProtobufGeneratedCodecHelpers.RequireSnapshotWriteCount(count, written);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufDictionaryOperation.Parser, "dictionary operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(
                    keyConverter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Key, "key", command)),
                    valueConverter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Value, "value", command)));
                break;
            case RemoveCommand:
                consumer.ApplyRemove(keyConverter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Key, "key", command)));
                ProtobufGeneratedCodecHelpers.RequireSingle(operation.Value.Count, "value", command);
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Count, "count", command);
                ProtobufGeneratedCodecHelpers.RequireSnapshotCount(count, operation.Value.Count, command);
                ProtobufGeneratedCodecHelpers.RequireSnapshotCount(count, operation.Key.Count, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(
                        keyConverter.FromByteString(operation.Key[i]),
                        valueConverter.FromByteString(operation.Value[i]));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
