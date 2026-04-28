using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable dictionary log entries.
/// </summary>
/// <example>
/// <code>
/// {"cmd":"set","key":"alice","value":42}
/// {"cmd":"snapshot","items":[{"key":"alice","value":42}]}
/// </code>
/// </example>
public sealed class JsonDictionaryEntryCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : IDurableDictionaryCodec<TKey, TValue> where TKey : notnull
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Set);
        writer.WritePropertyName(JsonLogEntryFields.Key);
        JsonSerializer.Serialize(writer, key, _options);
        writer.WritePropertyName(JsonLogEntryFields.Value);
        JsonSerializer.Serialize(writer, value, _options);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Remove);
        writer.WritePropertyName(JsonLogEntryFields.Key);
        JsonSerializer.Serialize(writer, key, _options);
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
    public void WriteSnapshot(IEnumerable<KeyValuePair<TKey, TValue>> items, int count, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var (key, value) in items)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(JsonLogEntryFields.Key);
            JsonSerializer.Serialize(writer, key, _options);
            writer.WritePropertyName(JsonLogEntryFields.Value);
            JsonSerializer.Serialize(writer, value, _options);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    root.GetProperty(JsonLogEntryFields.Key).Deserialize<TKey>(_options)!,
                    root.GetProperty(JsonLogEntryFields.Value).Deserialize<TValue>(_options)!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(root.GetProperty(JsonLogEntryFields.Key).Deserialize<TKey>(_options)!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = root.GetProperty(JsonLogEntryFields.Items);
                consumer.ApplySnapshotStart(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    consumer.ApplySnapshotItem(
                        item.GetProperty(JsonLogEntryFields.Key).Deserialize<TKey>(_options)!,
                        item.GetProperty(JsonLogEntryFields.Value).Deserialize<TValue>(_options)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
