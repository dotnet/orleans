using System.Buffers;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable list log entries.
/// </summary>
public sealed class ProtobufListOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableListOperationCodec<T>
{
    private const uint CommandField = 1;
    private const uint IndexField = 2;
    private const uint ItemField = 3;
    private const uint CountField = 4;

    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveAtCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, AddCommand);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, SetCommand);
        ProtobufWire.WriteUInt32Field(output, IndexField, (uint)index);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, InsertCommand);
        ProtobufWire.WriteUInt32Field(output, IndexField, (uint)index);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, RemoveAtCommand);
        ProtobufWire.WriteUInt32Field(output, IndexField, (uint)index);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufWire.GetSnapshotCount(items);
        ProtobufWire.WriteUInt32Field(output, CommandField, SnapshotCommand);
        ProtobufWire.WriteUInt32Field(output, CountField, (uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufWire.ThrowIfSnapshotItemCountExceeded(count, written);
            converter.WriteField(output, ItemField, item);
            written++;
        }

        ProtobufWire.RequireSnapshotWriteCount(count, written);
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
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var index = 0;
        var count = 0;
        var hasCommand = false;
        var hasIndex = false;
        var hasCount = false;
        var hasItem = false;
        var snapshotStarted = false;
        var snapshotItemCount = 0;
        T? item = default;

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
                case IndexField:
                    ProtobufWire.RequireCommand(hasCommand);
                    index = ProtobufWire.ReadNonNegativeInt32(ref reader, "index");
                    hasIndex = true;
                    break;
                case CountField:
                    ProtobufWire.RequireCommand(hasCommand);
                    count = ProtobufWire.ReadNonNegativeInt32(ref reader, "count");
                    hasCount = true;
                    break;
                case ItemField:
                    ProtobufWire.RequireCommand(hasCommand);
                    item = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasItem = true;
                    if (command == SnapshotCommand)
                    {
                        if (!snapshotStarted)
                        {
                            ProtobufWire.RequireField(hasCount, "count", command);
                            consumer.ApplySnapshotStart(count);
                            snapshotStarted = true;
                        }

                        consumer.ApplySnapshotItem(item);
                        snapshotItemCount++;
                        item = default;
                        hasItem = false;
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
            case AddCommand:
                consumer.ApplyAdd(ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case SetCommand:
                ProtobufWire.RequireField(hasIndex, "index", command);
                consumer.ApplySet(index, ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case InsertCommand:
                ProtobufWire.RequireField(hasIndex, "index", command);
                consumer.ApplyInsert(index, ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case RemoveAtCommand:
                ProtobufWire.RequireField(hasIndex, "index", command);
                consumer.ApplyRemoveAt(index);
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ProtobufWire.RequireField(hasCount, "count", command);
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

/// <summary>
/// Protocol Buffers codec for durable queue log entries.
/// </summary>
public sealed class ProtobufQueueOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableQueueOperationCodec<T>
{
    private const uint CommandField = 1;
    private const uint ItemField = 2;
    private const uint CountField = 3;

    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, EnqueueCommand);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteDequeue(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, DequeueCommand);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufWire.GetSnapshotCount(items);
        ProtobufWire.WriteUInt32Field(output, CommandField, SnapshotCommand);
        ProtobufWire.WriteUInt32Field(output, CountField, (uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufWire.ThrowIfSnapshotItemCountExceeded(count, written);
            converter.WriteField(output, ItemField, item);
            written++;
        }

        ProtobufWire.RequireSnapshotWriteCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var count = 0;
        var hasCommand = false;
        var hasCount = false;
        var hasItem = false;
        var snapshotStarted = false;
        var snapshotItemCount = 0;
        T? item = default;

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
                    count = ProtobufWire.ReadNonNegativeInt32(ref reader, "count");
                    hasCount = true;
                    break;
                case ItemField:
                    ProtobufWire.RequireCommand(hasCommand);
                    item = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasItem = true;
                    if (command == SnapshotCommand)
                    {
                        if (!snapshotStarted)
                        {
                            ProtobufWire.RequireField(hasCount, "count", command);
                            consumer.ApplySnapshotStart(count);
                            snapshotStarted = true;
                        }

                        consumer.ApplySnapshotItem(item);
                        snapshotItemCount++;
                        item = default;
                        hasItem = false;
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
            case EnqueueCommand:
                consumer.ApplyEnqueue(ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case DequeueCommand:
                consumer.ApplyDequeue();
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ProtobufWire.RequireField(hasCount, "count", command);
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

/// <summary>
/// Protocol Buffers codec for durable set log entries.
/// </summary>
public sealed class ProtobufSetOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableSetOperationCodec<T>
{
    private const uint CommandField = 1;
    private const uint ItemField = 2;
    private const uint CountField = 3;

    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, AddCommand);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, RemoveCommand);
        converter.WriteField(output, ItemField, item);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, ClearCommand);
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        var count = ProtobufWire.GetSnapshotCount(items);
        ProtobufWire.WriteUInt32Field(output, CommandField, SnapshotCommand);
        ProtobufWire.WriteUInt32Field(output, CountField, (uint)count);
        var written = 0;
        foreach (var item in items)
        {
            ProtobufWire.ThrowIfSnapshotItemCountExceeded(count, written);
            converter.WriteField(output, ItemField, item);
            written++;
        }

        ProtobufWire.RequireSnapshotWriteCount(count, written);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var count = 0;
        var hasCommand = false;
        var hasCount = false;
        var hasItem = false;
        var snapshotStarted = false;
        var snapshotItemCount = 0;
        T? item = default;

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
                    count = ProtobufWire.ReadNonNegativeInt32(ref reader, "count");
                    hasCount = true;
                    break;
                case ItemField:
                    ProtobufWire.RequireCommand(hasCommand);
                    item = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasItem = true;
                    if (command == SnapshotCommand)
                    {
                        if (!snapshotStarted)
                        {
                            ProtobufWire.RequireField(hasCount, "count", command);
                            consumer.ApplySnapshotStart(count);
                            snapshotStarted = true;
                        }

                        consumer.ApplySnapshotItem(item);
                        snapshotItemCount++;
                        item = default;
                        hasItem = false;
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
            case AddCommand:
                consumer.ApplyAdd(ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case RemoveCommand:
                consumer.ApplyRemove(ProtobufWire.RequireValue(hasItem, item, "item", command));
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            case SnapshotCommand:
                ProtobufWire.RequireField(hasCount, "count", command);
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
