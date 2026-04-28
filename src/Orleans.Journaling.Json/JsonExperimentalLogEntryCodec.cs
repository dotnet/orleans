using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

internal sealed class JsonExperimentalLogEntryCodec(JsonSerializerOptions? options = null) : ILogEntryCodec
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    public void WriteEntry<TEntry>(TEntry entry, IBufferWriter<byte> output)
        where TEntry : ILogEntry<TEntry>
    {
        using var jsonWriter = new Utf8JsonWriter(output);
        var writer = new JsonEntryWriter(jsonWriter, _options);
        writer.WriteCommand(TEntry.Tag, TEntry.Name);
        TEntry.Write(ref writer, entry);
        writer.Complete();
    }

    public LogEntryCommand ReadCommand(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var command = document.RootElement.GetProperty(JsonLogEntryFields.Command).GetString()
            ?? throw new InvalidOperationException("Malformed JSON log entry: command must be a string.");
        return new(null, command);
    }

    public void ApplyEntry<TEntry, TConsumer>(ReadOnlySequence<byte> input, TConsumer consumer)
        where TEntry : ILogEntry<TEntry, TConsumer>
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var entryReader = new JsonEntryReader(document.RootElement, _options);
        TEntry.Apply(ref entryReader, consumer);
    }

    private readonly struct JsonEntryWriter(Utf8JsonWriter writer, JsonSerializerOptions options) : ILogEntryWriter
    {
        public void WriteCommand(uint tag, string name)
        {
            writer.WriteStartObject();
            writer.WriteString(JsonLogEntryFields.Command, name);
        }

        public void WriteField<T>(uint tag, string name, T value)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, options);
        }

        public void WriteRepeated<T>(uint tag, string name, uint countTag, string countName, IEnumerable<T> values, int count)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var value in values)
            {
                JsonSerializer.Serialize(writer, value, options);
            }

            writer.WriteEndArray();
        }

        public void WriteKeyValuePairs<TKey, TValue>(
            uint tag,
            string name,
            uint countTag,
            string countName,
            uint keyTag,
            string keyName,
            uint valueTag,
            string valueName,
            IEnumerable<KeyValuePair<TKey, TValue>> values,
            int count)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var (key, value) in values)
            {
                writer.WriteStartObject();
                WriteField(keyTag, keyName, key);
                WriteField(valueTag, valueName, value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        public void Complete() => writer.WriteEndObject();
    }

    private readonly struct JsonEntryReader(JsonElement root, JsonSerializerOptions options) : ILogEntryReader
    {
        public T ReadField<T>(uint tag, string name) => root.GetProperty(name).Deserialize<T>(options)!;

        public void ReadRepeated<T>(uint tag, string name, uint countTag, string countName, Action<int> start, Action<T> item)
        {
            var items = root.GetProperty(name);
            start(items.GetArrayLength());
            foreach (var value in items.EnumerateArray())
            {
                item(value.Deserialize<T>(options)!);
            }
        }

        public void ReadKeyValuePairs<TKey, TValue>(
            uint tag,
            string name,
            uint countTag,
            string countName,
            uint keyTag,
            string keyName,
            uint valueTag,
            string valueName,
            Action<int> start,
            Action<TKey, TValue> item)
        {
            var items = root.GetProperty(name);
            start(items.GetArrayLength());
            foreach (var value in items.EnumerateArray())
            {
                item(
                    value.GetProperty(keyName).Deserialize<TKey>(options)!,
                    value.GetProperty(valueName).Deserialize<TValue>(options)!);
            }
        }
    }
}
