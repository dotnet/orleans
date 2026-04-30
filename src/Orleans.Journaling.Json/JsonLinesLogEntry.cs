using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json;

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonLinesLogEntry))]
internal partial class JsonLinesLogEntryJsonContext : JsonSerializerContext;

internal sealed class JsonLinesLogEntry
{
    private JsonElement _streamId;
    private JsonElement _entry;

    [JsonPropertyName("streamId")]
    public JsonElement StreamId
    {
        get => _streamId;
        set
        {
            if (HasStreamId)
            {
                DuplicatePropertyName ??= "streamId";
            }

            _streamId = value;
            HasStreamId = true;
        }
    }

    [JsonPropertyName("entry")]
    public JsonElement Entry
    {
        get => _entry;
        set
        {
            if (HasEntry)
            {
                DuplicatePropertyName ??= "entry";
            }

            _entry = value;
            HasEntry = true;
        }
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool HasStreamId { get; private set; }

    [JsonIgnore]
    public bool HasEntry { get; private set; }

    [JsonIgnore]
    public string? DuplicatePropertyName { get; private set; }
}
