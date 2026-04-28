using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable list log entries.
/// </summary>
public sealed class JsonListEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableListCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Add);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        JsonSerializer.Serialize(writer, item, _options);
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
        JsonSerializer.Serialize(writer, item, _options);
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
        JsonSerializer.Serialize(writer, item, _options);
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
    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            JsonSerializer.Serialize(writer, item, _options);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableListLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
                break;
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(root.GetProperty(JsonLogEntryFields.Index).GetInt32(), root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
                break;
            case JsonLogEntryCommands.Insert:
                consumer.ApplyInsert(root.GetProperty(JsonLogEntryFields.Index).GetInt32(), root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
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
                    consumer.ApplySnapshotItem(item.Deserialize<T>(_options)!);
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
public sealed class JsonQueueEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableQueueCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteEnqueue(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Enqueue);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        JsonSerializer.Serialize(writer, item, _options);
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
    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            JsonSerializer.Serialize(writer, item, _options);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableQueueLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Enqueue:
                consumer.ApplyEnqueue(root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
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
                    consumer.ApplySnapshotItem(item.Deserialize<T>(_options)!);
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
public sealed class JsonSetEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableSetCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteAdd(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Add);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        JsonSerializer.Serialize(writer, item, _options);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteRemove(T item, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Remove);
        writer.WritePropertyName(JsonLogEntryFields.Item);
        JsonSerializer.Serialize(writer, item, _options);
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
    public void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var item in items)
        {
            JsonSerializer.Serialize(writer, item, _options);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableSetLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Add:
                consumer.ApplyAdd(root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(root.GetProperty(JsonLogEntryFields.Item).Deserialize<T>(_options)!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = root.GetProperty(JsonLogEntryFields.Items);
                consumer.ApplySnapshotStart(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    consumer.ApplySnapshotItem(item.Deserialize<T>(_options)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
