namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Stream queue storage adapter for ADO.NET providers.
/// </summary>
internal partial class AdoNetQueueAdapter(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, AdoNetStreamQueueMapper mapper, RelationalOrleansQueries queries, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapter> logger, IServiceProvider serviceProvider) : IQueueAdapter
{
    private readonly ILogger<AdoNetQueueAdapter> _logger = logger;

    /// <summary>
    /// Maps to the ProviderId in the database.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// The ADO.NET provider is not yet rewindable.
    /// </summary>
    public bool IsRewindable => false;

    /// <summary>
    /// The ADO.NET provider works both ways.
    /// </summary>
    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        // map the queue id
        var adoNetQueueId = mapper.GetAdoNetQueueId(queueId);

        // create the receiver
        return ReceiverFactory(serviceProvider, [Name, adoNetQueueId, streamOptions, clusterOptions, cacheOptions, queries]);
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        // the ADO.NET provider is not rewindable so we do not support user supplied tokens
        if (token is not null)
        {
            throw new ArgumentException($"{nameof(AdoNetQueueAdapter)} does not support a user supplied {nameof(StreamSequenceToken)}.");
        }

        // map the Orleans stream id to the corresponding queue id
        var queueId = mapper.GetAdoNetQueueId(streamId);

        // create the payload from the events
        var payload = AdoNetBatchContainer.ToMessagePayload(serializer, streamId, events.Cast<object>().ToList(), requestContext);

        // we can enqueue the message now
        try
        {
            await queries.QueueStreamMessageAsync(clusterOptions.ServiceId, Name, queueId, payload, streamOptions.ExpiryTimeout.TotalSecondsCeiling());
        }
        catch (Exception ex)
        {
            LogFailedToQueueStreamMessage(ex, clusterOptions.ServiceId, Name, queueId);
            throw;
        }
    }

    /// <summary>
    /// The receiver factory.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapterReceiver> ReceiverFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterReceiver>([typeof(string), typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(RelationalOrleansQueries)]);

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to queue stream message with ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogFailedToQueueStreamMessage(Exception ex, string serviceId, string providerId, string queueId);

    #endregion Logging
}