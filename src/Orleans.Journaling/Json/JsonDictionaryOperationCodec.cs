using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable dictionary journal entries.
/// </summary>
/// <example>
/// <code>
/// ["set","alice",42]
/// ["snapshot",[["alice",42]]]
/// </code>
/// </example>
public sealed class JsonDictionaryOperationCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : IDictionaryOperationCodec<TKey, TValue>, IJsonJournalEntryCodec where TKey : notnull
{
    private readonly JsonTypeInfo<TKey> _keyTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TKey>(options);
    private readonly JsonTypeInfo<TValue> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TValue>(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new SetOperation(_keyTypeInfo, _valueTypeInfo, key, value),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new RemoveOperation(_keyTypeInfo, key),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);

        JsonOperationCodecWriter.Write(
            writer,
            new SnapshotOperation(_keyTypeInfo, _valueTypeInfo, items),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDictionaryOperationHandler<TKey, TValue> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    operation.DeserializeRequired(1, JsonJournalEntryFields.Key, _keyTypeInfo),
                    operation.DeserializeRequired(2, JsonJournalEntryFields.Value, _valueTypeInfo));
                operation.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Remove:
                consumer.ApplyRemove(operation.DeserializeRequired(1, JsonJournalEntryFields.Key, _keyTypeInfo));
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
                    var (key, value) = operation.ReadCurrentPairRequired(JsonJournalEntryFields.Items, _keyTypeInfo, _valueTypeInfo);
                    consumer.ApplySet(key, value);
                }

                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state)
    {
        if (state is not IDictionaryOperationHandler<TKey, TValue> consumer)
        {
            throw new InvalidOperationException(
                $"State '{state.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private readonly struct SetOperation(JsonTypeInfo<TKey> keyTypeInfo, JsonTypeInfo<TValue> valueTypeInfo, TKey key, TValue value)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonJournalEntryCommands.Set);
            JsonSerializer.Serialize(writer, key, keyTypeInfo);
            JsonSerializer.Serialize(writer, value, valueTypeInfo);
        }
    }

    private readonly struct RemoveOperation(JsonTypeInfo<TKey> keyTypeInfo, TKey key)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonJournalEntryCommands.Remove);
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

            writer.WriteStringValue(JsonJournalEntryCommands.Snapshot);
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
