namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The <see cref="IBatchContainer"/> implementation for the ADONET provider.
/// </summary>
/// <remarks>
/// 1. This class only supports binary serialization as performance and data size is the priority for database storage.
/// 2. Though the <see cref="SequenceToken"/> is supported here, it is not yet used, as the ADONET provider is not yet rewindable.
/// </remarks>
[GenerateSerializer]
internal class AdoNetBatchContainer : IBatchContainer
{
    public AdoNetBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
    {
        ArgumentNullException.ThrowIfNull(events);

        StreamId = streamId;
        Events = events;
        RequestContext = requestContext;
    }

    #region Serialized State

    [Id(0)]
    public StreamId StreamId { get; }

    [Id(1)]
    public List<object> Events { get; }

    [Id(2)]
    private Dictionary<string, object> RequestContext { get; }

    [Id(3)]
    public EventSequenceTokenV2 SequenceToken { get; internal set; }

    #endregion Serialized State

    #region Interface

    StreamSequenceToken IBatchContainer.SequenceToken => SequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => Events
        .OfType<T>()
        .Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, SequenceToken.CreateSequenceTokenForEvent(i)));

    public bool ImportRequestContext()
    {
        if (RequestContext is not null)
        {
            RequestContextExtensions.Import(RequestContext);
            return true;
        }

        return false;
    }

    #endregion Interface

    public override string ToString() => $"[{nameof(AdoNetBatchContainer)}:Stream={StreamId},#Items={Events.Count}]";
}