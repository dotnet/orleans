using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.Streaming.AdoNet;

[GenerateSerializer]
internal class AdoNetBatchContainer : IBatchContainer
{
    public AdoNetBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
    {
        ArgumentNullException.ThrowIfNull(events);

        StreamId = streamId;
        _events = events;
        _requestContext = requestContext;
    }

    #region Serialized State

    [Id(0)]
    private readonly EventSequenceTokenV2 _sequenceToken;

    [Id(1)]
    private readonly List<object> _events;

    [Id(2)]
    private readonly Dictionary<string, object> _requestContext;

    [Id(3)]
    public StreamId StreamId { get; }

    #endregion Serialized State

    public StreamSequenceToken SequenceToken => _sequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => _events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, _sequenceToken.CreateSequenceTokenForEvent(i)));

    public bool ImportRequestContext()
    {
        if (_requestContext is not null)
        {
            RequestContextExtensions.Import(_requestContext);
            return true;
        }

        return false;
    }

    public override string ToString() => $"[{nameof(AdoNetBatchContainer)}:Stream={StreamId},#Items={_events.Count}]";
}