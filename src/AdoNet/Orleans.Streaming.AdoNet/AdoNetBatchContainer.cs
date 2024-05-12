namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The <see cref="IBatchContainer"/> implementation for the ADONET provider.
/// </summary>
/// <remarks>
/// 1. This class only supports binary serialization as performance and data size is the priority for database storage.
/// 2. Though the <see cref="SequenceToken"/> is supported here, it is not yet used, as the ADO.NET provider is not rewindable.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.Streaming.AdoNet.AdoNetBatchContainer")]
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
    public Dictionary<string, object> RequestContext { get; }

    [Id(3)]
    public EventSequenceTokenV2 SequenceToken { get; internal set; } = null!;

    /// <summary>
    /// Holds the receipt for message confirmation.
    /// </summary>
    [Id(4)]
    public int Dequeued { get; internal set; }

    #endregion Serialized State

    #region Interface

    StreamSequenceToken IBatchContainer.SequenceToken => SequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        return SequenceToken is null
            ? throw new InvalidOperationException($"Cannot get events from a half-baked {nameof(AdoNetBatchContainer)}")
            : Events
                .OfType<T>()
                .Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, SequenceToken.CreateSequenceTokenForEvent(i)));
    }

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

    #region Conversion

    /// <summary>
    /// Creates a new <see cref="AdoNetBatchContainer"/> from the specified <see cref="AdoNetStreamMessage"/>.
    /// </summary>
    public static AdoNetBatchContainer FromMessage(Serializer<AdoNetBatchContainer> serializer, AdoNetStreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(message);

        var container = serializer.Deserialize(message.Payload);
        container.SequenceToken = new(message.MessageId);
        container.Dequeued = message.Dequeued;

        return container;
    }

    /// <summary>
    /// Converts the specified <see cref="AdoNetBatchContainer"/> to a message payload.
    /// </summary>
    public static byte[] ToMessagePayload(Serializer<AdoNetBatchContainer> serializer, StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(events);

        var container = new AdoNetBatchContainer(streamId, events, requestContext);
        var payload = serializer.SerializeToArray(container);

        return payload;
    }

    #endregion Conversion

    public override string ToString() => $"[{nameof(AdoNetBatchContainer)}:Stream={StreamId},#Items={Events.Count}]";
}