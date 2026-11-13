using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Serializes a <see cref="TaskId"/> as its escaped hierarchical path, as a JSON string or property name.
/// </summary>
/// <remarks>
/// <see cref="TaskId"/> names this converter in its <see cref="JsonConverterAttribute"/>, so reflection-based and
/// source-generated serialization use it without registration. The empty identifier, <see cref="TaskId.None"/>, is the
/// empty string.
/// </remarks>
public sealed class TaskIdJsonConverter : JsonConverter<TaskId>
{
    /// <inheritdoc/>
    public override TaskId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
            && TaskId.TryParse(reader.GetString(), out var result))
        {
            return result;
        }

        throw new JsonException("A durable task identifier must be an escaped hierarchical path.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TaskId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    /// <inheritdoc/>
    public override TaskId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    /// <inheritdoc/>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TaskId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString());
    }
}
