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
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonListOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableListOperationHandler<T> consumer) => Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonListOperation), consumer);

    private void Apply(JsonListOperation operation, IDurableListOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(operation.Index.GetValueOrDefault(), operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.Insert:
                consumer.ApplyInsert(operation.Index.GetValueOrDefault(), operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(operation.Index.GetValueOrDefault());
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.Reset(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplyAdd(item.Deserialize(_itemTypeInfo)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableListOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
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
    public void WriteEnqueue(T item, LogStreamWriter writer) => Write(writer, CreateEnqueueOperation(item));

    private JsonQueueOperation CreateEnqueueOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Enqueue,
            Item = JsonSerializer.SerializeToElement(item, _itemTypeInfo)
        };
    }

    /// <inheritdoc/>
    public void WriteDequeue(LogStreamWriter writer) => Write(writer, CreateDequeueOperation());

    private static JsonQueueOperation CreateDequeueOperation() => new() { Command = JsonLogEntryCommands.Dequeue };

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => Write(writer, CreateClearOperation());

    private static JsonQueueOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(writer, CreateSnapshotOperation(items));
    }

    private JsonQueueOperation CreateSnapshotOperation(IReadOnlyCollection<T> items)
    {
        var snapshotItems = new JsonElement[items.Count];
        var index = 0;
        foreach (var item in items)
        {
            snapshotItems[index++] = JsonSerializer.SerializeToElement(item, _itemTypeInfo);
        }

        return new()
        {
            Command = JsonLogEntryCommands.Snapshot,
            Items = snapshotItems
        };
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonQueueOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableQueueOperationHandler<T> consumer) => Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonQueueOperation), consumer);

    private void Apply(JsonQueueOperation operation, IDurableQueueOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Enqueue:
                consumer.ApplyEnqueue(operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.Dequeue:
                consumer.ApplyDequeue();
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.Reset(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplyEnqueue(item.Deserialize(_itemTypeInfo)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableQueueOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }

    private static void Write(LogStreamWriter writer, JsonQueueOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonQueueOperationConverter.WriteArrayElements(jsonWriter, operation));
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
    public void WriteAdd(T item, LogStreamWriter writer) => Write(writer, CreateAddOperation(item));

    private JsonSetOperation CreateAddOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Add,
            Item = JsonSerializer.SerializeToElement(item, _itemTypeInfo)
        };
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, LogStreamWriter writer) => Write(writer, CreateRemoveOperation(item));

    private JsonSetOperation CreateRemoveOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Remove,
            Item = JsonSerializer.SerializeToElement(item, _itemTypeInfo)
        };
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => Write(writer, CreateClearOperation());

    private static JsonSetOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(writer, CreateSnapshotOperation(items));
    }

    private JsonSetOperation CreateSnapshotOperation(IReadOnlyCollection<T> items)
    {
        var snapshotItems = new JsonElement[items.Count];
        var index = 0;
        foreach (var item in items)
        {
            snapshotItems[index++] = JsonSerializer.SerializeToElement(item, _itemTypeInfo);
        }

        return new()
        {
            Command = JsonLogEntryCommands.Snapshot,
            Items = snapshotItems
        };
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonSetOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableSetOperationHandler<T> consumer) => Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonSetOperation), consumer);

    private void Apply(JsonSetOperation operation, IDurableSetOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(operation.Item.GetValueOrDefault().Deserialize(_itemTypeInfo)!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.Reset(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplyAdd(item.Deserialize(_itemTypeInfo)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableSetOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }

    private static void Write(LogStreamWriter writer, JsonSetOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonSetOperationConverter.WriteArrayElements(jsonWriter, operation));
    }
}
