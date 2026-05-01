using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonDictionaryOperation))]
[JsonSerializable(typeof(JsonDictionarySnapshotItem))]
[JsonSerializable(typeof(JsonListOperation))]
[JsonSerializable(typeof(JsonQueueOperation))]
[JsonSerializable(typeof(JsonSetOperation))]
[JsonSerializable(typeof(JsonValueOperation))]
[JsonSerializable(typeof(JsonStateOperation))]
[JsonSerializable(typeof(JsonTaskCompletionSourceOperation))]
internal partial class JsonOperationCodecsJsonContext : JsonSerializerContext;

[JsonConverter(typeof(JsonDictionaryOperationConverter))]
internal struct JsonDictionaryOperation
{
    public string? Command { get; set; }

    public JsonElement? Key { get; set; }

    public JsonElement? Value { get; set; }

    public JsonDictionarySnapshotItem[]? Items { get; set; }
}

internal struct JsonDictionarySnapshotItem
{
    public JsonElement Key { get; set; }

    public JsonElement Value { get; set; }
}

[JsonConverter(typeof(JsonListOperationConverter))]
internal struct JsonListOperation
{
    public string? Command { get; set; }

    public int? Index { get; set; }

    public JsonElement? Item { get; set; }

    public JsonElement[]? Items { get; set; }
}

[JsonConverter(typeof(JsonQueueOperationConverter))]
internal struct JsonQueueOperation
{
    public string? Command { get; set; }

    public JsonElement? Item { get; set; }

    public JsonElement[]? Items { get; set; }
}

[JsonConverter(typeof(JsonSetOperationConverter))]
internal struct JsonSetOperation
{
    public string? Command { get; set; }

    public JsonElement? Item { get; set; }

    public JsonElement[]? Items { get; set; }
}

[JsonConverter(typeof(JsonValueOperationConverter))]
internal struct JsonValueOperation
{
    public string? Command { get; set; }

    public JsonElement? Value { get; set; }
}

[JsonConverter(typeof(JsonStateOperationConverter))]
internal struct JsonStateOperation
{
    public string? Command { get; set; }

    public JsonElement? State { get; set; }

    public ulong? Version { get; set; }
}

[JsonConverter(typeof(JsonTaskCompletionSourceOperationConverter))]
internal struct JsonTaskCompletionSourceOperation
{
    public string? Command { get; set; }

    public JsonElement? Value { get; set; }

    public string? Message { get; set; }
}

internal sealed class JsonDictionaryOperationConverter : JsonConverter<JsonDictionaryOperation>
{
    public override JsonDictionaryOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonDictionaryOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Set:
                operation.Key = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Key);
                operation.Value = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Value);
                break;
            case JsonLogEntryCommands.Remove:
                operation.Key = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Key);
                break;
            case JsonLogEntryCommands.Snapshot:
                operation.Items = JsonOperationArrayCodec.ReadDictionarySnapshotItems(ref reader);
                break;
            case JsonLogEntryCommands.Clear:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonDictionaryOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonDictionaryOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Set:
                JsonOperationArrayCodec.WriteElement(writer, operation.Key);
                JsonOperationArrayCodec.WriteElement(writer, operation.Value);
                break;
            case JsonLogEntryCommands.Remove:
                JsonOperationArrayCodec.WriteElement(writer, operation.Key);
                break;
            case JsonLogEntryCommands.Snapshot:
                JsonOperationArrayCodec.WriteDictionarySnapshotItems(writer, operation.Items);
                break;
        }
    }
}

internal sealed class JsonListOperationConverter : JsonConverter<JsonListOperation>
{
    public override JsonListOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonListOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Add:
                operation.Item = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Item);
                break;
            case JsonLogEntryCommands.Set:
            case JsonLogEntryCommands.Insert:
                operation.Index = JsonOperationArrayCodec.ReadInt32(ref reader, JsonLogEntryFields.Index);
                operation.Item = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Item);
                break;
            case JsonLogEntryCommands.RemoveAt:
                operation.Index = JsonOperationArrayCodec.ReadInt32(ref reader, JsonLogEntryFields.Index);
                break;
            case JsonLogEntryCommands.Snapshot:
                operation.Items = JsonOperationArrayCodec.ReadElementArray(ref reader, JsonLogEntryFields.Items);
                break;
            case JsonLogEntryCommands.Clear:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonListOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonListOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Add:
                JsonOperationArrayCodec.WriteElement(writer, operation.Item);
                break;
            case JsonLogEntryCommands.Set:
            case JsonLogEntryCommands.Insert:
                writer.WriteNumberValue(operation.Index.GetValueOrDefault());
                JsonOperationArrayCodec.WriteElement(writer, operation.Item);
                break;
            case JsonLogEntryCommands.RemoveAt:
                writer.WriteNumberValue(operation.Index.GetValueOrDefault());
                break;
            case JsonLogEntryCommands.Snapshot:
                JsonOperationArrayCodec.WriteElementArray(writer, operation.Items);
                break;
        }
    }
}

internal sealed class JsonQueueOperationConverter : JsonConverter<JsonQueueOperation>
{
    public override JsonQueueOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonQueueOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Enqueue:
                operation.Item = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Item);
                break;
            case JsonLogEntryCommands.Snapshot:
                operation.Items = JsonOperationArrayCodec.ReadElementArray(ref reader, JsonLogEntryFields.Items);
                break;
            case JsonLogEntryCommands.Dequeue:
            case JsonLogEntryCommands.Clear:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonQueueOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonQueueOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Enqueue:
                JsonOperationArrayCodec.WriteElement(writer, operation.Item);
                break;
            case JsonLogEntryCommands.Snapshot:
                JsonOperationArrayCodec.WriteElementArray(writer, operation.Items);
                break;
        }
    }
}

internal sealed class JsonSetOperationConverter : JsonConverter<JsonSetOperation>
{
    public override JsonSetOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonSetOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Add:
            case JsonLogEntryCommands.Remove:
                operation.Item = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Item);
                break;
            case JsonLogEntryCommands.Snapshot:
                operation.Items = JsonOperationArrayCodec.ReadElementArray(ref reader, JsonLogEntryFields.Items);
                break;
            case JsonLogEntryCommands.Clear:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonSetOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonSetOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Add:
            case JsonLogEntryCommands.Remove:
                JsonOperationArrayCodec.WriteElement(writer, operation.Item);
                break;
            case JsonLogEntryCommands.Snapshot:
                JsonOperationArrayCodec.WriteElementArray(writer, operation.Items);
                break;
        }
    }
}

internal sealed class JsonValueOperationConverter : JsonConverter<JsonValueOperation>
{
    public override JsonValueOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonValueOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Set:
                operation.Value = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Value);
                break;
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonValueOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonValueOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        if (operation.Command == JsonLogEntryCommands.Set)
        {
            JsonOperationArrayCodec.WriteElement(writer, operation.Value);
        }
    }
}

internal sealed class JsonStateOperationConverter : JsonConverter<JsonStateOperation>
{
    public override JsonStateOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonStateOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Set:
                operation.State = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.State);
                operation.Version = JsonOperationArrayCodec.ReadUInt64(ref reader, JsonLogEntryFields.Version);
                break;
            case JsonLogEntryCommands.Clear:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonStateOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonStateOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        if (operation.Command == JsonLogEntryCommands.Set)
        {
            JsonOperationArrayCodec.WriteElement(writer, operation.State);
            writer.WriteNumberValue(operation.Version.GetValueOrDefault());
        }
    }
}

internal sealed class JsonTaskCompletionSourceOperationConverter : JsonConverter<JsonTaskCompletionSourceOperation>
{
    public override JsonTaskCompletionSourceOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var command = JsonOperationArrayCodec.ReadCommand(ref reader);
        var operation = new JsonTaskCompletionSourceOperation { Command = command };

        switch (command)
        {
            case JsonLogEntryCommands.Completed:
                operation.Value = JsonOperationArrayCodec.ReadElement(ref reader, JsonLogEntryFields.Value);
                break;
            case JsonLogEntryCommands.Faulted:
                operation.Message = JsonOperationArrayCodec.ReadString(ref reader, JsonLogEntryFields.Message);
                break;
            case JsonLogEntryCommands.Pending:
            case JsonLogEntryCommands.Canceled:
            case null:
                break;
        }

        JsonOperationArrayCodec.EnsureEndArray(ref reader);
        return operation;
    }

    public override void Write(Utf8JsonWriter writer, JsonTaskCompletionSourceOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        WriteArrayElements(writer, value);
        writer.WriteEndArray();
    }

    internal static void WriteArrayElements(Utf8JsonWriter writer, JsonTaskCompletionSourceOperation operation)
    {
        writer.WriteStringValue(operation.Command);
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Completed:
                JsonOperationArrayCodec.WriteElement(writer, operation.Value);
                break;
            case JsonLogEntryCommands.Faulted:
                writer.WriteStringValue(operation.Message);
                break;
        }
    }
}

internal static class JsonOperationArrayCodec
{
    public static string? ReadCommand(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A JSON journal operation must be an array.");
        }

        if (!reader.Read())
        {
            throw new JsonException("A JSON journal operation array is incomplete.");
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException("A JSON journal operation array must include a command string.");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("The first JSON journal operation element must be a command string.");
        }

        return reader.GetString();
    }

    public static JsonElement ReadElement(ref Utf8JsonReader reader, string operandName)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public static int ReadInt32(ref Utf8JsonReader reader, string operandName)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        if (!reader.TryGetInt32(out var value))
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be a 32-bit integer.");
        }

        return value;
    }

    public static ulong ReadUInt64(ref Utf8JsonReader reader, string operandName)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        if (!reader.TryGetUInt64(out var value))
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be an unsigned integer.");
        }

        return value;
    }

    public static string? ReadString(ref Utf8JsonReader reader, string operandName)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be a string.");
        }

        return reader.GetString();
    }

    public static JsonElement[] ReadElementArray(ref Utf8JsonReader reader, string operandName)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be an array.");
        }

        var items = new List<JsonElement>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return [.. items];
            }

            using var document = JsonDocument.ParseValue(ref reader);
            items.Add(document.RootElement.Clone());
        }

        throw new JsonException($"JSON journal operation operand '{operandName}' array is incomplete.");
    }

    public static JsonDictionarySnapshotItem[] ReadDictionarySnapshotItems(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal operation is missing operand '{JsonLogEntryFields.Items}'.");
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"JSON journal operation operand '{JsonLogEntryFields.Items}' must be an array.");
        }

        var items = new List<JsonDictionarySnapshotItem>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return [.. items];
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("JSON dictionary snapshot items must be [key,value] arrays.");
            }

            var key = ReadElement(ref reader, JsonLogEntryFields.Key);
            var value = ReadElement(ref reader, JsonLogEntryFields.Value);
            EnsureEndArray(ref reader);
            items.Add(new() { Key = key, Value = value });
        }

        throw new JsonException($"JSON journal operation operand '{JsonLogEntryFields.Items}' array is incomplete.");
    }

    public static void EnsureEndArray(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("JSON journal operation array is incomplete.");
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("JSON journal operation contains unexpected extra elements.");
        }
    }

    public static void WriteElement(Utf8JsonWriter writer, JsonElement? element)
    {
        if (element is { } value)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    public static void WriteElementArray(Utf8JsonWriter writer, JsonElement[]? items)
    {
        writer.WriteStartArray();
        if (items is not null)
        {
            foreach (var item in items)
            {
                item.WriteTo(writer);
            }
        }

        writer.WriteEndArray();
    }

    public static void WriteDictionarySnapshotItems(Utf8JsonWriter writer, JsonDictionarySnapshotItem[]? items)
    {
        writer.WriteStartArray();
        if (items is not null)
        {
            foreach (var item in items)
            {
                writer.WriteStartArray();
                item.Key.WriteTo(writer);
                item.Value.WriteTo(writer);
                writer.WriteEndArray();
            }
        }

        writer.WriteEndArray();
    }
}
