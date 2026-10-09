using System.Text.Json.Serialization;

namespace Orleans.DurableMessaging.Tests.Functional;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(DateTime))]
internal partial class StandardStateJsonContext : JsonSerializerContext;
