using System.Buffers;
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
    public void WriteAdd(T item, JournalStreamWriter writer) => WriteItem(writer, JsonJournalEntryCommands.Add, item);

    /// <inheritdoc/>
    public void WriteSet(int index, T item, JournalStreamWriter writer) => WriteIndexedItem(writer, JsonJournalEntryCommands.Set, index, item);

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, JournalStreamWriter writer) => WriteIndexedItem(writer, JsonJournalEntryCommands.Insert, index, item);

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, JournalStreamWriter writer) => WriteIndex(writer, JsonJournalEntryCommands.RemoveAt, index);

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) => WriteCommand(writer, JsonJournalEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IListOperationHandler<T> consumer)
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

    private void WriteItem(JournalStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private void WriteIndexedItem(JournalStreamWriter writer, string command, int index, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new IndexedItemOperation(_itemTypeInfo, command, index, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteIndex(JournalStreamWriter writer, string command, int index)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new IndexOperation(command, index),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(JournalStreamWriter writer, string command)
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

            writer.WriteStringValue(JsonJournalEntryCommands.Snapshot);
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
/// JSON codec for durable queue journal entries.
/// </summary>
public sealed class JsonQueueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IQueueOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, JournalStreamWriter writer) => WriteItem(writer, JsonJournalEntryCommands.Enqueue, item);

    /// <inheritdoc/>
    public void WriteDequeue(JournalStreamWriter writer) => WriteCommand(writer, JsonJournalEntryCommands.Dequeue);

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) => WriteCommand(writer, JsonJournalEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IQueueOperationHandler<T> consumer)
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

    private void WriteItem(JournalStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(JournalStreamWriter writer, string command)
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

            writer.WriteStringValue(JsonJournalEntryCommands.Snapshot);
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
/// JSON codec for durable set journal entries.
/// </summary>
public sealed class JsonSetOperationCodec<T>(JsonSerializerOptions? options = null)
    : ISetOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer) => WriteItem(writer, JsonJournalEntryCommands.Add, item);

    /// <inheritdoc/>
    public void WriteRemove(T item, JournalStreamWriter writer) => WriteItem(writer, JsonJournalEntryCommands.Remove, item);

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer) => WriteCommand(writer, JsonJournalEntryCommands.Clear);

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_itemTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, ISetOperationHandler<T> consumer)
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

    private void WriteItem(JournalStreamWriter writer, string command, T item)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new ItemOperation(_itemTypeInfo, command, item),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    private static void WriteCommand(JournalStreamWriter writer, string command)
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

            writer.WriteStringValue(JsonJournalEntryCommands.Snapshot);
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
