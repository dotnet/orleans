using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Reminders.Redis;

internal static class RedisReminderSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Preserve the legacy Newtonsoft.Json escaping used for Redis lexicographic ranges.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly RedisReminderJsonContext JsonContext = new(JsonOptions);

    internal static string SerializeMember(params string[] segments)
    {
        var serialized = JsonSerializer.Serialize(segments, JsonContext.StringArray);
        return serialized[1..^1];
    }

    internal static string[] DeserializeMember(string value)
    {
        return JsonSerializer.Deserialize($"[{value}]", JsonContext.StringArray)
            ?? throw new JsonException("Redis reminder entry deserialized to null.");
    }

    internal static (string From, string To) GetFilter(params string[] segments)
    {
        var prefix = SerializeMember(segments);
        return ($"{prefix},\"", $"{prefix},#");
    }
}

[JsonSerializable(typeof(string[]), TypeInfoPropertyName = "StringArray")]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = false)]
internal partial class RedisReminderJsonContext : JsonSerializerContext;
