using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// MessagePack codec for durable list log entries.
/// </summary>
public sealed class MessagePackListEntryCodec<T>(MessagePackSerializerOptions options) : IDurableListCodec<T>
{
    private const int AddCommand = 0;
    private const int SetCommand = 1;
    private const int InsertCommand = 2;
    private const int RemoveAtCommand = 3;
    private const int ClearCommand = 4;
    private const int SnapshotCommand = 5;

    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(AddCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteSet(int index, T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(SetCommand);
        writer.Write(index);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteInsert(int index, T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(InsertCommand);
        writer.Write(index);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteRemoveAt(int index, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(RemoveAtCommand);
        writer.Write(index);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteClear(IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(ClearCommand);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2 + count);
        writer.Write(SnapshotCommand);
        writer.Write(count);
        foreach (var item in items)
        {
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        }

        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableListLogEntryConsumer<T> consumer)
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
            case AddCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyAdd(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case SetCommand:
                RequireItemCount(itemCount, 3, command);
                consumer.ApplySet(reader.ReadInt32(), MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case InsertCommand:
                RequireItemCount(itemCount, 3, command);
                consumer.ApplyInsert(reader.ReadInt32(), MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case RemoveAtCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyRemoveAt(reader.ReadInt32());
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
                MessagePackCodecHelpers.RequireSnapshotCount(count, itemCount - 2, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
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

/// <summary>
/// MessagePack codec for durable queue log entries.
/// </summary>
public sealed class MessagePackQueueEntryCodec<T>(MessagePackSerializerOptions options) : IDurableQueueCodec<T>
{
    private const int EnqueueCommand = 0;
    private const int DequeueCommand = 1;
    private const int ClearCommand = 2;
    private const int SnapshotCommand = 3;

    public void WriteEnqueue(T item, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(EnqueueCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteDequeue(IBufferWriter<byte> output) => WriteCommand(DequeueCommand, output);

    public void WriteClear(IBufferWriter<byte> output) => WriteCommand(ClearCommand, output);

    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2 + count);
        writer.Write(SnapshotCommand);
        writer.Write(count);
        foreach (var item in items)
        {
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        }

        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableQueueLogEntryConsumer<T> consumer)
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
            case EnqueueCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyEnqueue(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case DequeueCommand:
                RequireItemCount(itemCount, 1, command);
                consumer.ApplyDequeue();
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
                MessagePackCodecHelpers.RequireSnapshotCount(count, itemCount - 2, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
    }

    private static void WriteCommand(int command, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    private static void RequireItemCount(int actual, int expected, int command)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} expected {expected} item(s), found {actual}.");
        }
    }
}

/// <summary>
/// MessagePack codec for durable set log entries.
/// </summary>
public sealed class MessagePackSetEntryCodec<T>(MessagePackSerializerOptions options) : IDurableSetCodec<T>
{
    private const int AddCommand = 0;
    private const int RemoveCommand = 1;
    private const int ClearCommand = 2;
    private const int SnapshotCommand = 3;

    public void WriteAdd(T item, IBufferWriter<byte> output) => WriteItemCommand(AddCommand, item, output);

    public void WriteRemove(T item, IBufferWriter<byte> output) => WriteItemCommand(RemoveCommand, item, output);

    public void WriteClear(IBufferWriter<byte> output) => WriteCommand(ClearCommand, output);

    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2 + count);
        writer.Write(SnapshotCommand);
        writer.Write(count);
        foreach (var item in items)
        {
            MessagePackCodecHelpers.WriteValue(ref writer, item, options);
        }

        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableSetLogEntryConsumer<T> consumer)
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
            case AddCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyAdd(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case RemoveCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyRemove(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
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
                MessagePackCodecHelpers.RequireSnapshotCount(count, itemCount - 2, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
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

    private static void RequireItemCount(int actual, int expected, int command)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} expected {expected} item(s), found {actual}.");
        }
    }
}
