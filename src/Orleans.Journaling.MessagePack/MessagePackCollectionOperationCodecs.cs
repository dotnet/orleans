using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// MessagePack codec for durable list log entries.
/// </summary>
public sealed class MessagePackListOperationCodec<T>(MessagePackSerializerOptions options) : IDurableListOperationCodec<T>
{
    private const int AddCommand = 0;
    private const int SetCommand = 1;
    private const int InsertCommand = 2;
    private const int RemoveAtCommand = 3;
    private const int ClearCommand = 4;
    private const int SnapshotCommand = 5;

    public void WriteAdd(T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteAddPayload(item, output));

    private void WriteAddPayload(T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(AddCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteSet(int index, T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSetPayload(index, item, output));

    private void WriteSetPayload(int index, T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(SetCommand);
        writer.Write(index);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteInsert(int index, T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteInsertPayload(index, item, output));

    private void WriteInsertPayload(int index, T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(InsertCommand);
        writer.Write(index);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteRemoveAt(int index, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteRemoveAtPayload(index, output));

    private static void WriteRemoveAtPayload(int index, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(RemoveAtCommand);
        writer.Write(index);
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

    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = MessagePackCodecHelpers.GetSnapshotCount(items);
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(MessagePackCodecHelpers.GetSnapshotArrayHeaderCount(count, 1));
        writer.Write(SnapshotCommand);
        writer.Write(count);
        var written = 0;
        foreach (var item in items)
        {
            MessagePackCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
            written++;
        }

        MessagePackCodecHelpers.RequireSnapshotWriteCount(count, written);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case AddCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyAdd(reader.ReadValue<T>(options));
                break;
            case SetCommand:
                reader.RequireOperandCount(2);
                consumer.ApplySet(reader.ReadInt32(), reader.ReadValue<T>(options));
                break;
            case InsertCommand:
                reader.RequireOperandCount(2);
                consumer.ApplyInsert(reader.ReadInt32(), reader.ReadValue<T>(options));
                break;
            case RemoveAtCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyRemoveAt(reader.ReadInt32());
                break;
            case ClearCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = reader.ReadSnapshotCount(valuesPerItem: 1);
                consumer.Reset(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplyAdd(reader.ReadValue<T>(options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }
}

/// <summary>
/// MessagePack codec for durable queue log entries.
/// </summary>
public sealed class MessagePackQueueOperationCodec<T>(MessagePackSerializerOptions options) : IDurableQueueOperationCodec<T>
{
    private const int EnqueueCommand = 0;
    private const int DequeueCommand = 1;
    private const int ClearCommand = 2;
    private const int SnapshotCommand = 3;

    public void WriteEnqueue(T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteEnqueuePayload(item, output));

    private void WriteEnqueuePayload(T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(EnqueueCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteDequeue(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCommand(DequeueCommand, output));

    public void WriteClear(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCommand(ClearCommand, output));

    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = MessagePackCodecHelpers.GetSnapshotCount(items);
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(MessagePackCodecHelpers.GetSnapshotArrayHeaderCount(count, 1));
        writer.Write(SnapshotCommand);
        writer.Write(count);
        var written = 0;
        foreach (var item in items)
        {
            MessagePackCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
            written++;
        }

        MessagePackCodecHelpers.RequireSnapshotWriteCount(count, written);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case EnqueueCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyEnqueue(reader.ReadValue<T>(options));
                break;
            case DequeueCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyDequeue();
                break;
            case ClearCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = reader.ReadSnapshotCount(valuesPerItem: 1);
                consumer.Reset(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplyEnqueue(reader.ReadValue<T>(options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }

    private static void WriteCommand(int command, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        MessagePackCodecHelpers.Flush(ref writer);
    }

}

/// <summary>
/// MessagePack codec for durable set log entries.
/// </summary>
public sealed class MessagePackSetOperationCodec<T>(MessagePackSerializerOptions options) : IDurableSetOperationCodec<T>
{
    private const int AddCommand = 0;
    private const int RemoveCommand = 1;
    private const int ClearCommand = 2;
    private const int SnapshotCommand = 3;

    public void WriteAdd(T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteItemCommand(AddCommand, item, output));

    public void WriteRemove(T item, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteItemCommand(RemoveCommand, item, output));

    public void WriteClear(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCommand(ClearCommand, output));

    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSnapshotPayload(items, output));

    private void WriteSnapshotPayload(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = MessagePackCodecHelpers.GetSnapshotCount(items);
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(MessagePackCodecHelpers.GetSnapshotArrayHeaderCount(count, 1));
        writer.Write(SnapshotCommand);
        writer.Write(count);
        var written = 0;
        foreach (var item in items)
        {
            MessagePackCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
            written++;
        }

        MessagePackCodecHelpers.RequireSnapshotWriteCount(count, written);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case AddCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyAdd(reader.ReadValue<T>(options));
                break;
            case RemoveCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyRemove(reader.ReadValue<T>(options));
                break;
            case ClearCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = reader.ReadSnapshotCount(valuesPerItem: 1);
                consumer.Reset(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplyAdd(reader.ReadValue<T>(options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }

    private void WriteItemCommand(int command, T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(command);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    private static void WriteCommand(int command, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        MessagePackCodecHelpers.Flush(ref writer);
    }

}
