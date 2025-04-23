using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.NATS;

[Serializable]
[GenerateSerializer]
[method: JsonConstructor]
internal class NatsBatchContainer(
    StreamId streamId,
    object[] events,
    Dictionary<string, object>? requestContext,
    string? replyTo = null,
    EventSequenceTokenV2 sequenceToken = default!)
    : IBatchContainer
{
    [Id(0)]
    [field: JsonPropertyName("sid")]
    public StreamId StreamId { get; } = streamId;

    [Id(1)]
    [field: JsonPropertyName("stk")]
    public StreamSequenceToken SequenceToken { get; set; } = sequenceToken;

    [Id(2)]
    [field: JsonPropertyName("evts")]
    public object[] Events { get; } = events;

    [Id(3)]
    [field: JsonPropertyName("ctx")]
    public Dictionary<string, object>? RequestContext { get; } = requestContext;

    [Id(4)]
    [field: JsonPropertyName("rpt")]
    public string? ReplyTo { get; set; } = replyTo;

    public bool ImportRequestContext()
    {
        if (this.RequestContext is not null)
        {
            RequestContextExtensions.Import(this.RequestContext);
            return true;
        }

        return false;
    }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() =>
        this.Events.OfType<T>().Select((e, i) => Tuple.Create(e, this.SequenceToken));

    public override string ToString() => $"[NatsBatchContainer:Stream={StreamId},#Items={Events.Length}]";
}