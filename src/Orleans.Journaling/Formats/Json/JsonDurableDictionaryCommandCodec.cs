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
public sealed class JsonDurableDictionaryCommandCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : IDurableDictionaryCommandCodec<TKey, TValue> where TKey : notnull
{
    private readonly JsonTypeInfo<TKey> _keyTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TKey>(options);
    private readonly JsonTypeInfo<TValue> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TValue>(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (keyTypeInfo: _keyTypeInfo, valueTypeInfo: _valueTypeInfo, key, value),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, command.key, command.keyTypeInfo);
                JsonSerializer.Serialize(jsonWriter, command.value, command.valueTypeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (keyTypeInfo: _keyTypeInfo, key),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Remove);
                JsonSerializer.Serialize(jsonWriter, command.key, command.keyTypeInfo);
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
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);

        JsonCommandWriter.Write(
            writer,
            (keyTypeInfo: _keyTypeInfo, valueTypeInfo: _valueTypeInfo, items),
            static (jsonWriter, command) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(command.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var (key, value) in command.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    jsonWriter.WriteStartArray();
                    JsonSerializer.Serialize(jsonWriter, key, command.keyTypeInfo);
                    JsonSerializer.Serialize(jsonWriter, value, command.valueTypeInfo);
                    jsonWriter.WriteEndArray();
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer)
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

    private void Apply(ref JsonCommandReader reader, IDurableDictionaryCommandHandler<TKey, TValue> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    reader.DeserializeRequired(1, JsonJournalEntryFields.Key, _keyTypeInfo),
                    reader.DeserializeAllowNull(2, JsonJournalEntryFields.Value, _valueTypeInfo)!);
                reader.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Remove:
                consumer.ApplyRemove(reader.DeserializeRequired(1, JsonJournalEntryFields.Key, _keyTypeInfo));
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
                    var (key, value) = reader.ReadCurrentPairRequiredFirst(JsonJournalEntryFields.Items, _keyTypeInfo, _valueTypeInfo);
                    consumer.ApplySet(key, value!);
                }

                reader.EnsureEnd(2);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
