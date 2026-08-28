namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Stream queue storage adapter for ADO.NET providers.
/// </summary>
internal partial class AdoNetQueueAdapter : IQueueAdapter, IQueueAdapterCache
{
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly SimpleQueueCacheOptions _cacheOptions;
    private readonly AdoNetStreamQueueMapper _mapper;
    private readonly RelationalOrleansQueries _queries;
    private readonly Serializer<AdoNetBatchContainer> _serializer;
    private readonly ILogger<AdoNetQueueAdapter> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly QueueAdapterReceiverRegistry<AdoNetQueueAdapterReceiver> _receivers;

    public AdoNetQueueAdapter(
        string name,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        SimpleQueueCacheOptions cacheOptions,
        AdoNetStreamQueueMapper mapper,
        RelationalOrleansQueries queries,
        Serializer<AdoNetBatchContainer> serializer,
        ILogger<AdoNetQueueAdapter> logger,
        IServiceProvider serviceProvider)
    {
        Name = name;
        _streamOptions = streamOptions;
        _clusterOptions = clusterOptions;
        _cacheOptions = cacheOptions;
        _mapper = mapper;
        _queries = queries;
        _serializer = serializer;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _receivers = new QueueAdapterReceiverRegistry<AdoNetQueueAdapterReceiver>(CreateReceiverCore);
    }

    /// <summary>
    /// Maps to the ProviderId in the database.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The ADO.NET partitioned stream provider resumes from its durable partition checkpoint.
    /// </summary>
    public bool IsRewindable => false;

    /// <summary>
    /// The ADO.NET provider works both ways.
    /// </summary>
    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        => _receivers.GetOrCreate(queueId);

    public IQueueCache CreateQueueCache(QueueId queueId)
        => _receivers.GetOrCreate(queueId);

    public async Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken? token,
        Dictionary<string, object>? requestContext)
    {
        // Producer-supplied tokens are not supported. Replay positions are consumer-side tokens.
        if (token is not null)
        {
            throw new ArgumentException($"{nameof(AdoNetQueueAdapter)} does not support a user supplied {nameof(StreamSequenceToken)}.");
        }

        var queueId = _mapper.GetAdoNetQueueId(streamId);
        var payload = AdoNetBatchContainer.ToMessagePayload(
            _serializer,
            streamId,
            events.Cast<object>().ToList(),
            requestContext);

        try
        {
            await _queries.AppendStreamMessageAsync(
                _clusterOptions.ServiceId,
                Name,
                queueId,
                streamId.FullKey.ToArray(),
                streamId.Namespace.Length,
                payload);
        }
        catch (Exception exception)
        {
            LogFailedToAppendStreamMessage(
                exception,
                _clusterOptions.ServiceId,
                Name,
                queueId);
            throw;
        }
    }

    private AdoNetQueueAdapterReceiver CreateReceiverCore(QueueId queueId)
    {
        var receiver = ActivatorUtilities.CreateInstance<AdoNetQueueAdapterReceiver>(
            _serviceProvider,
            Name,
            _mapper.GetAdoNetQueueId(queueId),
            _streamOptions,
            _clusterOptions,
            _cacheOptions,
            _queries);
        receiver.OnShutdown = current => _receivers.Remove(queueId, current);
        return receiver;
    }

    [LoggerMessage(1, LogLevel.Error, "Failed to append stream message with ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogFailedToAppendStreamMessage(Exception ex, string serviceId, string providerId, string queueId);
}
