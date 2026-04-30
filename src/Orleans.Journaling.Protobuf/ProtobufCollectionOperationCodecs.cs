using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable list log entries.
/// </summary>
public sealed class ProtobufListOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableListOperationCodec<T>
{
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveAtCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(AddCommand);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(SetCommand);
        operation.Index.Add((uint)index);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(InsertCommand);
        operation.Index.Add((uint)index);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, IBufferWriter<byte> output)
    {
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(RemoveAtCommand);
        operation.Index.Add((uint)index);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(ClearCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufGeneratedCodecHelpers.GetSnapshotCount(items);
        var operation = new ProtobufCollectionOperation();
        operation.Command.Add(SnapshotCommand);
        operation.Count.Add((uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufGeneratedCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            operation.Item.Add(converter.ToByteString(item));
            written++;
        }

        ProtobufGeneratedCodecHelpers.RequireSnapshotWriteCount(count, written);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
        => ApplyCollection(input, new ListConsumer(consumer), converter);

    private readonly struct ListConsumer(IDurableListOperationHandler<T> consumer) : ICollectionConsumer<T>
    {
        public void ApplyAdd(T item) => consumer.ApplyAdd(item);
        public void ApplySet(int index, T item) => consumer.ApplySet(index, item);
        public void ApplyInsert(int index, T item) => consumer.ApplyInsert(index, item);
        public void ApplyRemoveAt(int index) => consumer.ApplyRemoveAt(index);
        public void ApplyClear() => consumer.ApplyClear();
        public void ApplySnapshotStart(int count) => consumer.ApplySnapshotStart(count);
        public void ApplySnapshotItem(T item) => consumer.ApplySnapshotItem(item);
    }

    internal static void ApplyCollection<TConsumer>(ReadOnlySequence<byte> input, TConsumer consumer, ProtobufValueConverter<T> converter)
        where TConsumer : struct, ICollectionConsumer<T>
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufCollectionOperation.Parser, "collection operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case AddCommand:
                consumer.ApplyAdd(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case SetCommand:
                consumer.ApplySet(
                    ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Index, "index", command),
                    converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case InsertCommand:
                consumer.ApplyInsert(
                    ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Index, "index", command),
                    converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case RemoveAtCommand:
                consumer.ApplyRemoveAt(ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Index, "index", command));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Count, "count", command);
                ProtobufGeneratedCodecHelpers.RequireSnapshotCount(count, operation.Item.Count, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(converter.FromByteString(operation.Item[i]));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

/// <summary>
/// Protocol Buffers codec for durable queue log entries.
/// </summary>
public sealed class ProtobufQueueOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableQueueOperationCodec<T>
{
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufQueueOperation();
        operation.Command.Add(EnqueueCommand);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteDequeue(IBufferWriter<byte> output)
    {
        var operation = new ProtobufQueueOperation();
        operation.Command.Add(DequeueCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        var operation = new ProtobufQueueOperation();
        operation.Command.Add(ClearCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufGeneratedCodecHelpers.GetSnapshotCount(items);
        var operation = new ProtobufQueueOperation();
        operation.Command.Add(SnapshotCommand);
        operation.Count.Add((uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufGeneratedCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            operation.Item.Add(converter.ToByteString(item));
            written++;
        }

        ProtobufGeneratedCodecHelpers.RequireSnapshotWriteCount(count, written);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufQueueOperation.Parser, "queue operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case EnqueueCommand:
                consumer.ApplyEnqueue(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case DequeueCommand:
                consumer.ApplyDequeue();
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Count, "count", command);
                ProtobufGeneratedCodecHelpers.RequireSnapshotCount(count, operation.Item.Count, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(converter.FromByteString(operation.Item[i]));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

/// <summary>
/// Protocol Buffers codec for durable set log entries.
/// </summary>
public sealed class ProtobufSetOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableSetOperationCodec<T>
{
    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufSetOperation();
        operation.Command.Add(AddCommand);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, IBufferWriter<byte> output)
    {
        var operation = new ProtobufSetOperation();
        operation.Command.Add(RemoveCommand);
        operation.Item.Add(converter.ToByteString(item));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        var operation = new ProtobufSetOperation();
        operation.Command.Add(ClearCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufGeneratedCodecHelpers.GetSnapshotCount(items);
        var operation = new ProtobufSetOperation();
        operation.Command.Add(SnapshotCommand);
        operation.Count.Add((uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufGeneratedCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
            operation.Item.Add(converter.ToByteString(item));
            written++;
        }

        ProtobufGeneratedCodecHelpers.RequireSnapshotWriteCount(count, written);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufSetOperation.Parser, "set operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case AddCommand:
                consumer.ApplyAdd(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case RemoveCommand:
                consumer.ApplyRemove(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Item, "item", command)));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                var count = ProtobufGeneratedCodecHelpers.RequireNonNegativeInt32(operation.Count, "count", command);
                ProtobufGeneratedCodecHelpers.RequireSnapshotCount(count, operation.Item.Count, command);
                consumer.ApplySnapshotStart(count);
                for (var i = 0; i < count; i++)
                {
                    consumer.ApplySnapshotItem(converter.FromByteString(operation.Item[i]));
                }

                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

internal interface ICollectionConsumer<T>
{
    void ApplyAdd(T item);
    void ApplySet(int index, T item);
    void ApplyInsert(int index, T item);
    void ApplyRemoveAt(int index);
    void ApplyClear();
    void ApplySnapshotStart(int count);
    void ApplySnapshotItem(T item);
}
