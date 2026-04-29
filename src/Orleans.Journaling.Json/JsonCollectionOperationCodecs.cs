using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list log entries.
/// </summary>
public sealed class JsonListOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableListOperationCodec<T>
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Add);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteSet(int index, T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Set);
        writer.WriteNumber(JsonLogEntryFields.Index, index);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteInsert(int index, T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Insert);
        writer.WriteNumber(JsonLogEntryFields.Index, index);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteRemoveAt(int index, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.RemoveAt);
        writer.WriteNumber(JsonLogEntryFields.Index, index);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Clear);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            _itemSerializer.Serialize(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(_itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(root.GetProperty(JsonLogEntryFields.Index).GetInt32(), _itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.Insert:
                consumer.ApplyInsert(root.GetProperty(JsonLogEntryFields.Index).GetInt32(), _itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.RemoveAt:
                consumer.ApplyRemoveAt(root.GetProperty(JsonLogEntryFields.Index).GetInt32());
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = root.GetProperty(JsonLogEntryFields.Items);
                consumer.ApplySnapshotStart(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable queue log entries.
/// </summary>
public sealed class JsonQueueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableQueueOperationCodec<T>
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteEnqueue(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Enqueue);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteDequeue(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Dequeue);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Clear);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            _itemSerializer.Serialize(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Enqueue:
                consumer.ApplyEnqueue(_itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.Dequeue:
                consumer.ApplyDequeue();
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = root.GetProperty(JsonLogEntryFields.Items);
                consumer.ApplySnapshotStart(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable set log entries.
/// </summary>
public sealed class JsonSetOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableSetOperationCodec<T>
{
    private readonly JsonValueSerializer<T> _itemSerializer = new(options);

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Add);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Remove);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        _itemSerializer.Serialize(writer, item);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Clear);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            _itemSerializer.Serialize(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(_itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(_itemSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Item))!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = root.GetProperty(JsonLogEntryFields.Items);
                consumer.ApplySnapshotStart(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    consumer.ApplySnapshotItem(_itemSerializer.Deserialize(item)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
