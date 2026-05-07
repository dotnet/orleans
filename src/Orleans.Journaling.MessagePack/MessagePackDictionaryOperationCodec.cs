using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// MessagePack codec for durable dictionary log entries.
/// </summary>
public sealed class MessagePackDictionaryOperationCodec<TKey, TValue>(MessagePackSerializerOptions options) : IDurableDictionaryOperationCodec<TKey, TValue>
    where TKey : notnull
{
    private const int SetCommand = 0;
    private const int RemoveCommand = 1;
    private const int ClearCommand = 2;
    private const int SnapshotCommand = 3;

    public void WriteSet(TKey key, TValue value, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSetPayload(key, value, output));

    private void WriteSetPayload(TKey key, TValue value, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(SetCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, key, options);
        MessagePackCodecHelpers.WriteValue(ref writer, value, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteRemove(TKey key, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteRemovePayload(key, output));

    private void WriteRemovePayload(TKey key, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(RemoveCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, key, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteClear(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(ClearCommand);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        var count = MessagePackCodecHelpers.GetSnapshotCount(items);
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(MessagePackCodecHelpers.GetSnapshotArrayHeaderCount(count, 2));
        writer.Write(SnapshotCommand);
        writer.Write(count);
        var written = 0;
        foreach (var (key, value) in items)
        {
            MessagePackCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            MessagePackCodecHelpers.WriteValue(ref writer, key, options);
            MessagePackCodecHelpers.WriteValue(ref writer, value, options);
            written++;
        }

        MessagePackCodecHelpers.RequireSnapshotWriteCount(count, written);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case SetCommand:
                reader.RequireOperandCount(2);
                consumer.ApplySet(
                    reader.ReadValue<TKey>(options),
                    reader.ReadValue<TValue>(options));
                break;
            case RemoveCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyRemove(reader.ReadValue<TKey>(options));
                break;
            case ClearCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = reader.ReadSnapshotCount(2, "Malformed MessagePack dictionary snapshot: key/value item count is not balanced.");
                consumer.Reset(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySet(
                        reader.ReadValue<TKey>(options),
                        reader.ReadValue<TValue>(options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }
}
