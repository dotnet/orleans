using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list journal entries.
/// </summary>
public sealed class JsonListOperationCodec<T>(JsonSerializerOptions? options = null)
    : IListOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Add, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Set, index, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                jsonWriter.WriteNumberValue(operation.index);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Insert, index, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                jsonWriter.WriteNumberValue(operation.index);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (command: JsonJournalEntryCommands.RemoveAt, index),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                jsonWriter.WriteNumberValue(operation.index);
            });
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, operation) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(operation.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in operation.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, operation.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IListOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IListOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Add:
                consumer.ApplyAdd(operation.DeserializeRequired(1, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    operation.ReadInt32(1, JsonJournalEntryFields.Index),
                    operation.DeserializeRequired(2, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Insert:
                consumer.ApplyInsert(
                    operation.ReadInt32(1, JsonJournalEntryFields.Index),
                    operation.DeserializeRequired(2, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(operation.ReadInt32(1, JsonJournalEntryFields.Index));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAdd(operation.DeserializeCurrentRequired(JsonJournalEntryFields.Item, _itemTypeInfo));
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
/// <summary>
/// JSON codec for durable queue journal entries.
/// </summary>
public sealed class JsonQueueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IQueueOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Enqueue, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteDequeue(JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            JsonJournalEntryCommands.Dequeue,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, operation) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(operation.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in operation.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, operation.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IQueueOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IQueueOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Enqueue:
                consumer.ApplyEnqueue(operation.DeserializeRequired(1, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Dequeue:
                operation.EnsureEnd(1);
                consumer.ApplyDequeue();
                break;
            case JsonJournalEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyEnqueue(operation.DeserializeCurrentRequired(JsonJournalEntryFields.Item, _itemTypeInfo));
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable set journal entries.
/// </summary>
public sealed class JsonSetOperationCodec<T>(JsonSerializerOptions? options = null)
    : ISetOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Add, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Remove, item),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(operation.command);
                JsonSerializer.Serialize(jsonWriter, operation.item, operation.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonOperationWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, operation) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(operation.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in operation.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, operation.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, ISetOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, ISetOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Add:
                consumer.ApplyAdd(operation.DeserializeRequired(1, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Remove:
                consumer.ApplyRemove(operation.DeserializeRequired(1, JsonJournalEntryFields.Item, _itemTypeInfo));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAdd(operation.DeserializeCurrentRequired(JsonJournalEntryFields.Item, _itemTypeInfo));
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
