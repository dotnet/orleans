using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    private readonly JsonTypeInfo<TKey> _keyTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TKey>(options);
    private readonly JsonTypeInfo<TValue> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TValue>(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new SetOperation(_keyTypeInfo, _valueTypeInfo, key, value),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new RemoveOperation(_keyTypeInfo, key),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            JsonLogEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);

        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_keyTypeInfo, _valueTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
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
                    operation.Key.GetValueOrDefault().Deserialize(_keyTypeInfo)!,
                    operation.Value.GetValueOrDefault().Deserialize(_valueTypeInfo)!);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(operation.Key.GetValueOrDefault().Deserialize(_keyTypeInfo)!);
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var items = operation.Items ?? [];
                consumer.Reset(items.Length);
                foreach (var item in items)
                {
                    consumer.ApplySet(
                        item.Key.Deserialize(_keyTypeInfo)!,
                        item.Value.Deserialize(_valueTypeInfo)!);
                }

                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
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

    private readonly struct SetOperation(JsonTypeInfo<TKey> keyTypeInfo, JsonTypeInfo<TValue> valueTypeInfo, TKey key, TValue value)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonLogEntryCommands.Set);
            JsonSerializer.Serialize(writer, key, keyTypeInfo);
            JsonSerializer.Serialize(writer, value, valueTypeInfo);
        }
    }

    private readonly struct RemoveOperation(JsonTypeInfo<TKey> keyTypeInfo, TKey key)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonLogEntryCommands.Remove);
            JsonSerializer.Serialize(writer, key, keyTypeInfo);
        }
    }

    private readonly struct SnapshotOperation(
        JsonTypeInfo<TKey> keyTypeInfo,
        JsonTypeInfo<TValue> valueTypeInfo,
        IReadOnlyCollection<KeyValuePair<TKey, TValue>> items)
    {
        public void Write(Utf8JsonWriter writer)
        {
            var count = CollectionCodecHelpers.GetSnapshotCount(items);

            writer.WriteStringValue(JsonLogEntryCommands.Snapshot);
            writer.WriteStartArray();
            var written = 0;
            foreach (var (key, value) in items)
            {
                CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                writer.WriteStartArray();
                JsonSerializer.Serialize(writer, key, keyTypeInfo);
                JsonSerializer.Serialize(writer, value, valueTypeInfo);
                writer.WriteEndArray();
                written++;
            }

            CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
            writer.WriteEndArray();
        }
    }
}
