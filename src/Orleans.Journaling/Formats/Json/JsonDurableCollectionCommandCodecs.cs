using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list journal entries.
/// </summary>
public sealed class JsonDurableListCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableListCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Add, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Set, index, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                jsonWriter.WriteNumberValue(command.index);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Insert, index, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                jsonWriter.WriteNumberValue(command.index);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (command: JsonJournalEntryCommands.RemoveAt, index),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                jsonWriter.WriteNumberValue(command.index);
            });
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in command.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, command.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableListCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableListCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Add:
                consumer.ApplyAdd(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    reader.ReadInt32(1, JsonJournalEntryFields.Index),
                    reader.DeserializeAllowNull(2, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Insert:
                consumer.ApplyInsert(
                    reader.ReadInt32(1, JsonJournalEntryFields.Index),
                    reader.DeserializeAllowNull(2, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(reader.ReadInt32(1, JsonJournalEntryFields.Index));
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Clear:
                reader.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = reader.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAdd(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                reader.EnsureEnd(2);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
/// <summary>
/// JSON codec for durable queue journal entries.
/// </summary>
public sealed class JsonDurableQueueCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableQueueCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Enqueue, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteDequeue(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Dequeue,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in command.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, command.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableQueueCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableQueueCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Enqueue:
                consumer.ApplyEnqueue(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Dequeue:
                reader.EnsureEnd(1);
                consumer.ApplyDequeue();
                break;
            case JsonJournalEntryCommands.Clear:
                reader.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = reader.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyEnqueue(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                reader.EnsureEnd(2);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable set journal entries.
/// </summary>
public sealed class JsonDurableSetCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableSetCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _itemTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Add, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, command: JsonJournalEntryCommands.Remove, item),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(command.command);
                JsonSerializer.Serialize(jsonWriter, command.item, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _itemTypeInfo, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var item in command.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    JsonSerializer.Serialize(jsonWriter, item, command.typeInfo);
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableSetCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableSetCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Add:
                consumer.ApplyAdd(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Remove:
                consumer.ApplyRemove(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Item, _itemTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Clear:
                reader.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonJournalEntryCommands.Snapshot:
                var count = reader.StartArray(1, JsonJournalEntryFields.Items);
                consumer.Reset(count);
                while (reader.ReadArrayItem(JsonJournalEntryFields.Items))
                {
                    consumer.ApplyAdd(reader.DeserializeCurrentAllowNull(JsonJournalEntryFields.Item, _itemTypeInfo)!);
                }

                reader.EnsureEnd(2);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
