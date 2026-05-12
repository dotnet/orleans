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
    : IDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    private readonly JsonTypeInfo<TKey> _keyTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TKey>(options);
    private readonly JsonTypeInfo<TValue> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<TValue>(options);

    /// <inheritdoc/>
    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            (keyTypeInfo: _keyTypeInfo, valueTypeInfo: _valueTypeInfo, key, value),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, operation.key, operation.keyTypeInfo);
                JsonSerializer.Serialize(jsonWriter, operation.value, operation.valueTypeInfo);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteRemove(TKey key, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            (keyTypeInfo: _keyTypeInfo, key),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Remove);
                JsonSerializer.Serialize(jsonWriter, operation.key, operation.keyTypeInfo);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);

        var formattedEntry = JsonFormattedJournalEntry.Create(
            (keyTypeInfo: _keyTypeInfo, valueTypeInfo: _valueTypeInfo, items),
            static (jsonWriter, operation) =>
            {
                var count = CollectionCodecHelpers.GetSnapshotCount(operation.items);

                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Snapshot);
                jsonWriter.WriteStartArray();
                var written = 0;
                foreach (var (key, value) in operation.items)
                {
                    CollectionCodecHelpers.ThrowIfSnapshotItemCountExceeded(count, written);
                    jsonWriter.WriteStartArray();
                    JsonSerializer.Serialize(jsonWriter, key, operation.keyTypeInfo);
                    JsonSerializer.Serialize(jsonWriter, value, operation.valueTypeInfo);
                    jsonWriter.WriteEndArray();
                    written++;
                }

                CollectionCodecHelpers.RequireSnapshotItemCount(count, written);
                jsonWriter.WriteEndArray();
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IDictionaryOperationHandler<TKey, TValue> consumer)
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
}
