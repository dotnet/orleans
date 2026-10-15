using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Storage;

/// <summary>
/// Source-generated JSON metadata for the task state and pending invocations which the adapter stores in JSON journals.
/// </summary>
[JsonSerializable(typeof(DurableTaskState))]
[JsonSerializable(typeof(PendingInvocation))]
[JsonSerializable(typeof(MessageId))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class DurableTaskJsonContext : JsonSerializerContext;

/// <summary>
/// Provides JSON metadata for <see cref="TaskId"/>, which journals use as the task state and logical time key.
/// </summary>
/// <remarks>
/// <see cref="TaskId"/> is declared in an assembly without a JSON dependency, so the adapter supplies its converter
/// through this resolver instead of a type-level converter attribute.
/// </remarks>
internal sealed class TaskIdJsonTypeInfoResolver : IJsonTypeInfoResolver
{
    public static TaskIdJsonTypeInfoResolver Instance { get; } = new();

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
        type == typeof(TaskId) ? JsonMetadataServices.CreateValueInfo<TaskId>(options, TaskIdJsonConverter.Instance) : null;
}

/// <summary>
/// Serializes <see cref="TaskId"/> as its escaped hierarchical path, as a JSON string or property name.
/// </summary>
internal sealed class TaskIdJsonConverter : JsonConverter<TaskId>
{
    public static TaskIdJsonConverter Instance { get; } = new();

    public override TaskId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
            && TaskId.TryParse(reader.GetString(), out var result))
        {
            return result;
        }

        throw new JsonException("A durable task identifier must be an escaped hierarchical path.");
    }

    public override void Write(Utf8JsonWriter writer, TaskId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override TaskId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TaskId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());
}
