using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable dictionary log entries.
/// </summary>
/// <example>
/// <code>
/// ["set","alice",42]
/// ["snapshot",[["alice",42]]]
/// </code>
/// </example>
public sealed class JsonDictionaryOperationCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : IDurableDictionaryOperationCodec<TKey, TValue>, IJsonLogEntryCodec where TKey : notnull
{
    private readonly JsonValueSerializer<TKey> _keySerializer = new(options);
    private readonly JsonValueSerializer<TValue> _valueSerializer = new(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, LogStreamWriter writer) => Write(writer, CreateSetOperation(key, value));

    private JsonDictionaryOperation CreateSetOperation(TKey key, TValue value)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            Key = _keySerializer.SerializeToElement(key),
            Value = _valueSerializer.SerializeToElement(value)
        };
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, LogStreamWriter writer) => Write(writer, CreateRemoveOperation(key));

    private JsonDictionaryOperation CreateRemoveOperation(TKey key)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Remove,
            Key = _keySerializer.SerializeToElement(key)
        };
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => Write(writer, CreateClearOperation());

    private static JsonDictionaryOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);

        Write(writer, CreateSnapshotOperation(items));
    }

    private JsonDictionaryOperation CreateSnapshotOperation(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items)
    {
        var snapshotItems = new JsonDictionarySnapshotItem[items.Count];
        var index = 0;
        foreach (var (key, value) in items)
        {
            snapshotItems[index++] = new()
            {
                Key = _keySerializer.SerializeToElement(key),
                Value = _valueSerializer.SerializeToElement(value)
            };
        }

        return new()
        {
            Command = JsonLogEntryCommands.Snapshot,
            Items = snapshotItems
        };
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonDictionaryOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableDictionaryOperationHandler<TKey, TValue> consumer) => Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonDictionaryOperation), consumer);

    private void Apply(JsonDictionaryOperation operation, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    _keySerializer.Deserialize(operation.Key.GetValueOrDefault())!,
                    _valueSerializer.Deserialize(operation.Value.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(_keySerializer.Deserialize(operation.Key.GetValueOrDefault())!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.ApplySnapshotStart(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplySnapshotItem(
                        _keySerializer.Deserialize(item.Key)!,
                        _valueSerializer.Deserialize(item.Value)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    private static void Write(LogStreamWriter writer, JsonDictionaryOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonDictionaryOperationConverter.WriteArrayElements(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonDictionaryOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonDictionaryOperation operation) => JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonDictionaryOperation);

    private static void WriteBytes(IBufferWriter<byte> output, JsonDictionaryOperation operation) => Write(output, operation);

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
