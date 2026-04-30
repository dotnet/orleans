using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonLinesLogEntry))]
internal partial class JsonLinesLogEntryJsonContext : JsonSerializerContext;

internal struct JsonLinesLogEntry
{
    [JsonRequired]
    [JsonPropertyName("streamId")]
    public ulong StreamId { get; set; }

    [JsonRequired]
    [JsonPropertyName("entry")]
    public JsonElement Entry { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
