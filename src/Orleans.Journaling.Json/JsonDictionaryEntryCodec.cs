using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableDictionaryEntry{TKey, TValue}"/>.
/// Serializes entries as JSON with a <c>"cmd"</c> discriminator.
/// </summary>
/// <example>
/// <code>
/// {"cmd":"set","key":"alice","value":42}
/// {"cmd":"snapshot","items":[{"key":"alice","value":42}]}
/// </code>
/// </example>
public sealed class JsonDictionaryEntryCodec<TKey, TValue>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableDictionaryEntry<TKey, TValue> entry, IBufferWriter<byte> output)
    {
        JsonDictionaryEntry jsonEntry = entry switch
        {
            DictionarySetEntry<TKey, TValue>(var key, var value) =>
                new JsonDictionarySetEntry(
                    JsonSerializer.SerializeToElement(key, _options),
                    JsonSerializer.SerializeToElement(value, _options)),
            DictionaryRemoveEntry<TKey, TValue>(var key) =>
                new JsonDictionaryRemoveEntry(JsonSerializer.SerializeToElement(key, _options)),
            DictionaryClearEntry<TKey, TValue> =>
                new JsonDictionaryClearEntry(),
            DictionarySnapshotEntry<TKey, TValue>(var items) =>
                new JsonDictionarySnapshotEntry(items.Select(kv =>
                    new JsonDictionarySnapshotItem(
                        JsonSerializer.SerializeToElement(kv.Key, _options),
                        JsonSerializer.SerializeToElement(kv.Value, _options))).ToList()),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableDictionaryEntry<TKey, TValue> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonDictionaryEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize dictionary entry.");

        return jsonEntry switch
        {
            JsonDictionarySetEntry(var key, var value) =>
                new DictionarySetEntry<TKey, TValue>(
                    key.Deserialize<TKey>(_options)!,
                    value.Deserialize<TValue>(_options)!),
            JsonDictionaryRemoveEntry(var key) =>
                new DictionaryRemoveEntry<TKey, TValue>(key.Deserialize<TKey>(_options)!),
            JsonDictionaryClearEntry =>
                new DictionaryClearEntry<TKey, TValue>(),
            JsonDictionarySnapshotEntry(var items) =>
                new DictionarySnapshotEntry<TKey, TValue>(items.Select(i =>
                    new KeyValuePair<TKey, TValue>(
                        i.Key.Deserialize<TKey>(_options)!,
                        i.Value.Deserialize<TValue>(_options)!)).ToList()),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}
