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
        var reader = new MessagePackReader(input);
        var itemCount = reader.ReadArrayHeader();
        if (itemCount == 0)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: missing command.");
        }

        var command = reader.ReadInt32();
        switch (command)
        {
            case SetCommand:
                RequireItemCount(itemCount, 3, command);
                consumer.ApplySet(
                    MessagePackCodecHelpers.ReadValue<TKey>(ref reader, options),
                    MessagePackCodecHelpers.ReadValue<TValue>(ref reader, options));
                break;
            case RemoveCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyRemove(MessagePackCodecHelpers.ReadValue<TKey>(ref reader, options));
                break;
            case ClearCommand:
                RequireItemCount(itemCount, 1, command);
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                if (itemCount < 2)
                {
                    throw new InvalidOperationException("Malformed MessagePack log entry: missing snapshot count.");
                }

                var count = reader.ReadInt32();
                MessagePackCodecHelpers.RequireSnapshotCount(count, (itemCount - 2) / 2, command);
                if ((itemCount - 2) % 2 != 0)
                {
                    throw new InvalidOperationException("Malformed MessagePack dictionary snapshot: key/value item count is not balanced.");
                }

                consumer.Reset(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySet(
                        MessagePackCodecHelpers.ReadValue<TKey>(ref reader, options),
                        MessagePackCodecHelpers.ReadValue<TValue>(ref reader, options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
    }

    private static void RequireItemCount(int actual, int expected, int command)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} expected {expected} item(s), found {actual}.");
        }
    }
}
