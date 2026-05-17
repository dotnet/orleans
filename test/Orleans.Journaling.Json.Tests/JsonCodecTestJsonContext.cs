using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json.Tests;

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ThrowingJsonValue))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(JsonCodecTestValue))]
[JsonSerializable(typeof(JournalingSnapshotRecord))]
internal partial class JsonCodecTestJsonContext : JsonSerializerContext;

internal sealed record JsonCodecTestValue(string Name, int Count);

/// <summary>
/// JSON-side mirror of <see cref="Orleans.Journaling.Tests.JournalingSnapshotRecord"/>: the
/// snapshot tests use logically-identical record shapes on both sides so the binary and JSONL
/// coverage matrices stay parallel.
/// </summary>
internal sealed record JournalingSnapshotRecord(string Name, int Count, JournalingSnapshotRecordTag Tag);

/// <summary>
/// JSON-side mirror of <see cref="Orleans.Journaling.Tests.JournalingSnapshotRecordTag"/>.
/// </summary>
internal readonly record struct JournalingSnapshotRecordTag(string Label, int Code);

[JsonConverter(typeof(ThrowingJsonValueConverter))]
internal sealed record ThrowingJsonValue(string Name);

internal sealed class ThrowingJsonValueConverter : JsonConverter<ThrowingJsonValue>
{
    public override ThrowingJsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ThrowingJsonValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(ThrowingJsonValue.Name), value.Name);
        throw new InvalidOperationException("The test converter failed after writing partial JSON.");
    }
}
