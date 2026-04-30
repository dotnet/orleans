using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list log entries.
/// </summary>
public sealed class JsonListOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableListOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output) => Write(output, CreateAddOperation(item));

    /// <inheritdoc/>
    public void WriteAdd(T item, LogWriter writer) => Write(writer, CreateAddOperation(item));

    private JsonListOperation CreateAddOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Add,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, IBufferWriter<byte> output) => Write(output, CreateSetOperation(index, item));

    /// <inheritdoc/>
    public void WriteSet(int index, T item, LogWriter writer) => Write(writer, CreateSetOperation(index, item));

    private JsonListOperation CreateSetOperation(int index, T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            Index = index,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, IBufferWriter<byte> output) => Write(output, CreateInsertOperation(index, item));

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, LogWriter writer) => Write(writer, CreateInsertOperation(index, item));

    private JsonListOperation CreateInsertOperation(int index, T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Insert,
            Index = index,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, IBufferWriter<byte> output) => Write(output, CreateRemoveAtOperation(index));

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, LogWriter writer) => Write(writer, CreateRemoveAtOperation(index));

    private static JsonListOperation CreateRemoveAtOperation(int index)
    {
        return new()
        {
            Command = JsonLogEntryCommands.RemoveAt,
            Index = index
        };
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output) => Write(output, CreateClearOperation());

    /// <inheritdoc/>
    public void WriteClear(LogWriter writer) => Write(writer, CreateClearOperation());

    private static JsonListOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(output, CreateSnapshotOperation(items));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(writer, CreateSnapshotOperation(items));
    }

    private JsonListOperation CreateSnapshotOperation(IReadOnlyCollection<T> items)
    {
        var snapshotItems = new JsonElement[items.Count];
        var index = 0;
        foreach (var item in items)
        {
            snapshotItems[index++] = _itemSerializer.SerializeToElement(item);
        }

        return new()
        {
            Command = JsonLogEntryCommands.Snapshot,
            Items = snapshotItems
        };
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
                consumer.ApplyAdd(_itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(operation.Index.GetValueOrDefault(), _itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Insert:
                consumer.ApplyInsert(operation.Index.GetValueOrDefault(), _itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(operation.Index.GetValueOrDefault());
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.ApplySnapshotStart(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
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

    private static void Write(LogWriter writer, JsonListOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => WriteJson(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonListOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonListOperation operation) => JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonListOperation);

    private static void WriteBytes(IBufferWriter<byte> output, JsonListOperation operation) => Write(output, operation);
}

/// <summary>
/// JSON codec for durable queue log entries.
/// </summary>
public sealed class JsonQueueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableQueueOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, IBufferWriter<byte> output) => Write(output, CreateEnqueueOperation(item));

    /// <inheritdoc/>
    public void WriteEnqueue(T item, LogWriter writer) => Write(writer, CreateEnqueueOperation(item));

    private JsonQueueOperation CreateEnqueueOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Enqueue,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteDequeue(IBufferWriter<byte> output) => Write(output, CreateDequeueOperation());

    /// <inheritdoc/>
    public void WriteDequeue(LogWriter writer) => Write(writer, CreateDequeueOperation());

    private static JsonQueueOperation CreateDequeueOperation() => new() { Command = JsonLogEntryCommands.Dequeue };

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output) => Write(output, CreateClearOperation());

    /// <inheritdoc/>
    public void WriteClear(LogWriter writer) => Write(writer, CreateClearOperation());

    private static JsonQueueOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(output, CreateSnapshotOperation(items));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer)
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
            snapshotItems[index++] = _itemSerializer.SerializeToElement(item);
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
                consumer.ApplyEnqueue(_itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Dequeue:
                consumer.ApplyDequeue();
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.ApplySnapshotStart(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
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

    private static void Write(LogWriter writer, JsonQueueOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => WriteJson(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonQueueOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonQueueOperation operation) => JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonQueueOperation);

    private static void WriteBytes(IBufferWriter<byte> output, JsonQueueOperation operation) => Write(output, operation);
}

/// <summary>
/// JSON codec for durable set log entries.
/// </summary>
public sealed class JsonSetOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableSetOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output) => Write(output, CreateAddOperation(item));

    /// <inheritdoc/>
    public void WriteAdd(T item, LogWriter writer) => Write(writer, CreateAddOperation(item));

    private JsonSetOperation CreateAddOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Add,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, IBufferWriter<byte> output) => Write(output, CreateRemoveOperation(item));

    /// <inheritdoc/>
    public void WriteRemove(T item, LogWriter writer) => Write(writer, CreateRemoveOperation(item));

    private JsonSetOperation CreateRemoveOperation(T item)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Remove,
            Item = _itemSerializer.SerializeToElement(item)
        };
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output) => Write(output, CreateClearOperation());

    /// <inheritdoc/>
    public void WriteClear(LogWriter writer) => Write(writer, CreateClearOperation());

    private static JsonSetOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);
        Write(output, CreateSnapshotOperation(items));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer)
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
            snapshotItems[index++] = _itemSerializer.SerializeToElement(item);
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
                consumer.ApplyAdd(_itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(_itemSerializer.Deserialize(operation.Item.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.ApplySnapshotStart(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
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

    private static void Write(LogWriter writer, JsonSetOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => WriteJson(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonSetOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonSetOperation operation) => JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonSetOperation);

    private static void WriteBytes(IBufferWriter<byte> output, JsonSetOperation operation) => Write(output, operation);
}
