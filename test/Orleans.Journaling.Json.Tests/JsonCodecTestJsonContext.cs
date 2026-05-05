using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json.Tests;

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(JsonCodecTestValue))]
internal partial class JsonCodecTestJsonContext : JsonSerializerContext;

internal sealed record JsonCodecTestValue(string Name, int Count);
