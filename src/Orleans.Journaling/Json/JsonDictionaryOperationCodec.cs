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
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    operation.Deserialize(1, JsonLogEntryFields.Key, _keyTypeInfo)!,
                    operation.Deserialize(2, JsonLogEntryFields.Value, _valueTypeInfo)!);
                operation.EnsureEnd(3);
                break;
            case JsonLogEntryCommands.Remove:
                consumer.ApplyRemove(operation.Deserialize(1, JsonLogEntryFields.Key, _keyTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            case JsonLogEntryCommands.Snapshot:
                var count = operation.StartArray(1, JsonLogEntryFields.Items);
                consumer.Reset(count);
                while (operation.ReadArrayItem(JsonLogEntryFields.Items))
                {
                    var (key, value) = operation.ReadCurrentPair(JsonLogEntryFields.Items, _keyTypeInfo, _valueTypeInfo);
                    consumer.ApplySet(key!, value!);
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableDictionaryOperationHandler<TKey, TValue> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
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
