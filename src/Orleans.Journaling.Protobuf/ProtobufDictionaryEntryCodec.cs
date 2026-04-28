using System.Buffers;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable dictionary log entries.
/// </summary>
public sealed class ProtobufDictionaryEntryCodec<TKey, TValue>(
    ProtobufValueConverter<TKey> keyConverter,
    ProtobufValueConverter<TValue> valueConverter) : IDurableDictionaryCodec<TKey, TValue> where TKey : notnull
{
    private const uint CommandField = 1;
    private const uint KeyField = 2;
    private const uint ValueField = 3;
    private const uint CountField = 4;

    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, SetCommand);
        keyConverter.WriteField(output, KeyField, key);
        valueConverter.WriteField(output, ValueField, value);
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, RemoveCommand);
        keyConverter.WriteField(output, KeyField, key);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IEnumerable<KeyValuePair<TKey, TValue>> items, int count, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, SnapshotCommand);
        ProtobufWire.WriteUInt32Field(output, CountField, (uint)count);
        foreach (var (key, value) in items)
        {
            keyConverter.WriteField(output, KeyField, key);
            valueConverter.WriteField(output, ValueField, value);
        }
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var count = 0;
        TKey? key = default;
        TValue? value = default;
        var hasCommand = false;
        var hasCount = false;
        var hasKey = false;
        var hasValue = false;
        var snapshotStarted = false;
        var snapshotItemCount = 0;

        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case CommandField:
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                    break;
                case CountField:
                    ProtobufWire.RequireCommand(hasCommand);
                    count = (int)ProtobufWire.ReadUInt32(ref reader);
                    hasCount = true;
                    break;
                case KeyField:
                    ProtobufWire.RequireCommand(hasCommand);
                    if (command == SnapshotCommand && hasKey)
                    {
                        ProtobufWire.RequireField(false, "value", command);
                    }

                    key = keyConverter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasKey = true;
                    if (command == SnapshotCommand && !snapshotStarted)
                    {
                        ProtobufWire.RequireField(hasCount, "count", command);
                        consumer.ApplySnapshotStart(count);
                        snapshotStarted = true;
                    }

                    break;
                case ValueField:
                    ProtobufWire.RequireCommand(hasCommand);
                    value = valueConverter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasValue = true;
                    if (command == SnapshotCommand)
                    {
                        if (!snapshotStarted)
                        {
                            ProtobufWire.RequireField(hasCount, "count", command);
                            consumer.ApplySnapshotStart(count);
                            snapshotStarted = true;
                        }

                        consumer.ApplySnapshotItem(
                            ProtobufWire.RequireValue(hasKey, key, "key", command),
                            value);
                        snapshotItemCount++;
                        key = default;
                        value = default;
                        hasKey = false;
                        hasValue = false;
                    }

                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }

        ProtobufWire.RequireCommand(hasCommand);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(
                    ProtobufWire.RequireValue(hasKey, key, "key", command),
                    ProtobufWire.RequireValue(hasValue, value, "value", command));
                break;
            case RemoveCommand:
                consumer.ApplyRemove(ProtobufWire.RequireValue(hasKey, key, "key", command));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ProtobufWire.RequireField(hasCount, "count", command);
                ProtobufWire.RequireField(!hasKey, "value", command);
                ProtobufWire.RequireSnapshotCount(count, snapshotItemCount, command);
                if (!snapshotStarted)
                {
                    consumer.ApplySnapshotStart(count);
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
