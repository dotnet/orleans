using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list log entries.
/// </summary>
public sealed class JsonListOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableListOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, LogStreamWriter writer) => WriteItem(writer, JsonLogEntryCommands.Add, item);

    /// <inheritdoc/>
    public void WriteSet(int index, T item, LogStreamWriter writer) => WriteIndexedItem(writer, JsonLogEntryCommands.Set, index, item);

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, LogStreamWriter writer) => WriteIndexedItem(writer, JsonLogEntryCommands.Insert, index, item);

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, LogStreamWriter writer) => WriteIndex(writer, JsonLogEntryCommands.RemoveAt, index);

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableListOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(operation.Deserialize(1, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    operation.ReadInt32(1, JsonLogEntryFields.Index),
                    operation.Deserialize(2, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(3);
                break;
            case JsonLogEntryCommands.Insert:
                consumer.ApplyInsert(
                    operation.ReadInt32(1, JsonLogEntryFields.Index),
                    operation.Deserialize(2, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(3);
                break;
            case JsonLogEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(operation.ReadInt32(1, JsonLogEntryFields.Index));
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonLogEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonLogEntryFields.Items))
                {
                    consumer.ApplyAdd(operation.DeserializeCurrent(_itemTypeInfo)!);
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableListOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private void WriteItem(LogStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private void WriteIndexedItem(LogStreamWriter writer, string command, int index, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new IndexedItemOperation(_itemTypeInfo, command, index, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteIndex(LogStreamWriter writer, string command, int index)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new IndexOperation(command, index),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(LogStreamWriter writer, string command)
    {
        JsonOperationCodecWriter.Write(
            writer,
            command,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    private readonly struct ItemOperation(JsonTypeInfo<T> typeInfo, string command, T item)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(command);
            JsonSerializer.Serialize(writer, item, typeInfo);
        }
    }

    private readonly struct IndexedItemOperation(JsonTypeInfo<T> typeInfo, string command, int index, T item)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(command);
            writer.WriteNumberValue(index);
            JsonSerializer.Serialize(writer, item, typeInfo);
        }
    }

    private readonly struct IndexOperation(string command, int index)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(command);
            writer.WriteNumberValue(index);
        }
    }

    private readonly struct SnapshotOperation(JsonTypeInfo<T> typeInfo, IReadOnlyCollection<T> items)
    {
        public void Write(Utf8JsonWriter writer)
        {
            var count = CollectionCodecHelpers.GetSnapshotCount(items);

            writer.WriteStringValue(JsonLogEntryCommands.Snapshot);
            writer.WriteStartArray();
            var written = 0;
            foreach (var item in items)
            {
                CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                JsonSerializer.Serialize(writer, item, typeInfo);
                written++;
            }

            CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
            writer.WriteEndArray();
        }
    }
}

/// <summary>
/// JSON codec for durable queue log entries.
/// </summary>
public sealed class JsonQueueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableQueueOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, LogStreamWriter writer) => WriteItem(writer, JsonLogEntryCommands.Enqueue, item);

    /// <inheritdoc/>
    public void WriteDequeue(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Dequeue);

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableQueueOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Enqueue:
                consumer.ApplyEnqueue(operation.Deserialize(1, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Dequeue:
                operation.EnsureEnd(1);
                consumer.ApplyDequeue();
                break;
            case JsonLogEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonLogEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonLogEntryFields.Items))
                {
                    consumer.ApplyEnqueue(operation.DeserializeCurrent(_itemTypeInfo)!);
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableQueueOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private void WriteItem(LogStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(LogStreamWriter writer, string command)
    {
        JsonOperationCodecWriter.Write(
            writer,
            command,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    private readonly struct ItemOperation(JsonTypeInfo<T> typeInfo, string command, T item)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(command);
            JsonSerializer.Serialize(writer, item, typeInfo);
        }
    }

    private readonly struct SnapshotOperation(JsonTypeInfo<T> typeInfo, IReadOnlyCollection<T> items)
    {
        public void Write(Utf8JsonWriter writer)
        {
            var count = CollectionCodecHelpers.GetSnapshotCount(items);

            writer.WriteStringValue(JsonLogEntryCommands.Snapshot);
            writer.WriteStartArray();
            var written = 0;
            foreach (var item in items)
            {
                CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                JsonSerializer.Serialize(writer, item, typeInfo);
                written++;
            }

            CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
            writer.WriteEndArray();
        }
    }
}

/// <summary>
/// JSON codec for durable set log entries.
/// </summary>
public sealed class JsonSetOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableSetOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, LogStreamWriter writer) => WriteItem(writer, JsonLogEntryCommands.Add, item);

    /// <inheritdoc/>
    public void WriteRemove(T item, LogStreamWriter writer) => WriteItem(writer, JsonLogEntryCommands.Remove, item);

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableSetOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(operation.Deserialize(1, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(operation.Deserialize(1, JsonLogEntryFields.Item, _itemTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonLogEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonLogEntryFields.Items))
                {
                    consumer.ApplyAdd(operation.DeserializeCurrent(_itemTypeInfo)!);
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableSetOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private void WriteItem(LogStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(LogStreamWriter writer, string command)
    {
        JsonOperationCodecWriter.Write(
            writer,
            command,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    private readonly struct ItemOperation(JsonTypeInfo<T> typeInfo, string command, T item)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(command);
            JsonSerializer.Serialize(writer, item, typeInfo);
        }
    }

    private readonly struct SnapshotOperation(JsonTypeInfo<T> typeInfo, IReadOnlyCollection<T> items)
    {
        public void Write(Utf8JsonWriter writer)
        {
            var count = CollectionCodecHelpers.GetSnapshotCount(items);

            writer.WriteStringValue(JsonLogEntryCommands.Snapshot);
            writer.WriteStartArray();
            var written = 0;
            foreach (var item in items)
            {
                CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                JsonSerializer.Serialize(writer, item, typeInfo);
                written++;
            }

            CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
            writer.WriteEndArray();
        }
    }
}
