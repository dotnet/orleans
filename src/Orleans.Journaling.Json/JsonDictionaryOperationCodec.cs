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
public sealed class JsonDictionaryOperationCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : IDurableDictionaryOperationCodec<TKey, TValue>, IJsonLogEntryCodec where TKey : notnull
{
    private readonly JsonValueSerializer<TKey> _keySerializer = new(options);
    private readonly JsonValueSerializer<TValue> _valueSerializer = new(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Set);
        writer.WritePropertyName(JsonLogEntryFields.Key);
        _keySerializer.Serialize(writer, key);
        writer.WritePropertyName(JsonLogEntryFields.Value);
        _valueSerializer.Serialize(writer, value);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Remove);
        writer.WritePropertyName(JsonLogEntryFields.Key);
        _keySerializer.Serialize(writer, key);
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
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Snapshot);
        writer.WritePropertyName(JsonLogEntryFields.Items);
        writer.WriteStartArray();
        foreach (var (key, value) in items)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(JsonLogEntryFields.Key);
            _keySerializer.Serialize(writer, key);
            writer.WritePropertyName(JsonLogEntryFields.Value);
            _valueSerializer.Serialize(writer, value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        Apply(root, consumer);
    }

    internal void Apply(JsonElement root, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    _keySerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Key))!,
                    _valueSerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Value))!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(_keySerializer.Deserialize(root.GetProperty(JsonLogEntryFields.Key))!);
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
                        _keySerializer.Deserialize(item.GetProperty(JsonLogEntryFields.Key))!,
                        _valueSerializer.Deserialize(item.GetProperty(JsonLogEntryFields.Value))!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableDictionaryOperationHandler<TKey, TValue> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }
}
