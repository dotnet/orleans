using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Streaming.Redis.Streams;

[GenerateSerializer]
[Alias(nameof(RedisStreamBatchContainer))]
internal class RedisStreamBatchContainer : IBatchContainer
{
    [Id(0)]
    public StreamId StreamId { get; set; }

    [Id(1)]
    public StreamSequenceToken SequenceToken { get; private set; }

    [Id(2)]
    public List<object> Events { get; set; } = [];

    [Id(3)]
    public Dictionary<string, object>? RequestContext { get; set; }

    [NonSerialized]
    internal EventSequenceTokenV2? SequenceTokenV2;

    internal EventSequenceTokenV2 RealSequenceToken
    {
        get
        {
            SequenceTokenV2 ??= (EventSequenceTokenV2)SequenceToken;
            return SequenceTokenV2;
        }
        set
        {
            SequenceTokenV2 = value;
            SequenceToken = SequenceTokenV2;
        }
    }

    public RedisStreamBatchContainer()
    {

    }

    public RedisStreamBatchContainer(StreamId streamId, StreamSequenceToken token, List<object> events, Dictionary<string, object> requestContext)
    {
        StreamId = streamId;
        SequenceToken = token;
        Events = events ?? throw new ArgumentNullException(nameof(events), "Message contains no events");
        RequestContext = requestContext;
    }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        return Events
            .OfType<T>()
            .Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, RealSequenceToken.CreateSequenceTokenForEvent(i)));
    }

    public bool ImportRequestContext()
    {
        if (RequestContext != null)
        {
            RequestContextExtensions.Import(RequestContext);
            return true;
        }
        return false;
    }
}
