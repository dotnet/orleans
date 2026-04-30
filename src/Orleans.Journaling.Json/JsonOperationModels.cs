using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(JsonDictionaryOperation))]
[JsonSerializable(typeof(JsonDictionarySnapshotItem))]
[JsonSerializable(typeof(JsonListOperation))]
[JsonSerializable(typeof(JsonQueueOperation))]
[JsonSerializable(typeof(JsonSetOperation))]
[JsonSerializable(typeof(JsonValueOperation))]
[JsonSerializable(typeof(JsonStateOperation))]
[JsonSerializable(typeof(JsonTaskCompletionSourceOperation))]
internal partial class JsonOperationCodecsJsonContext : JsonSerializerContext;

internal struct JsonDictionaryOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("key")]
    public JsonElement? Key { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("items")]
    public JsonDictionarySnapshotItem[]? Items { get; set; }
}

internal struct JsonDictionarySnapshotItem
{
    [JsonRequired]
    [JsonPropertyName("key")]
    public JsonElement Key { get; set; }

    [JsonRequired]
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

internal struct JsonListOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("item")]
    public JsonElement? Item { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("items")]
    public JsonElement[]? Items { get; set; }
}

internal struct JsonQueueOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("item")]
    public JsonElement? Item { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("items")]
    public JsonElement[]? Items { get; set; }
}

internal struct JsonSetOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("item")]
    public JsonElement? Item { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("items")]
    public JsonElement[]? Items { get; set; }
}

internal struct JsonValueOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

internal struct JsonStateOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("state")]
    public JsonElement? State { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("version")]
    public ulong? Version { get; set; }
}

internal struct JsonTaskCompletionSourceOperation
{
    [JsonRequired]
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
