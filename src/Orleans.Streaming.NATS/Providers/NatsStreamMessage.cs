using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Streaming.NATS;

[Serializable]
[GenerateSerializer]
internal class NatsStreamMessage
{
    [Id(0)]
    [JsonConverter(typeof(StreamIdJsonConverter))]
    [JsonPropertyName("sid")]
    public StreamId StreamId { get; set; }

    [Id(1)]
    [JsonPropertyName("ctx")]
    public Dictionary<string, object>? RequestContext { get; set; }

    [Id(2)]
    [JsonPropertyName("p")]
    public required byte[] Payload { get; set; }

    [Id(3)]
    [JsonPropertyName("rpt")]
    public string? ReplyTo { get; set; }
}

[JsonSerializable(typeof(NatsStreamMessage))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, WriteIndented = false)]
internal partial class NatsSerializerContext : JsonSerializerContext;