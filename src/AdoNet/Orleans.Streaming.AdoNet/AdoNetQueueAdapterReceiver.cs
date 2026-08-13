namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Receives message batches from an individual queue of an ADO.NET provider.
/// </summary>
internal class AdoNetQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly string _providerId;
    private readonly string _queueId;

    public AdoNetQueueAdapterReceiver(
        string providerId,
        string queueId,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        SimpleQueueCacheOptions cacheOptions,
        RelationalOrleansQueries queries,
        Serializer<AdoNetBatchContainer> serializer,
        ILogger<AdoNetQueueAdapterReceiver> logger)
    {
        _providerId = providerId;
        _queueId = queueId;
        _ = streamOptions;
        _ = clusterOptions;
        _ = cacheOptions;
        _ = queries;
        _ = serializer;
        _ = logger;
    }

    /// <summary>
    /// Receiver integration is supplied by the shared recoverable stream pipeline.
    /// </summary>
    public Task Initialize(TimeSpan timeout) => Task.FromException(CreatePipelineUnavailableException());

    /// <summary>
    /// No receiver work is started by this storage-only implementation.
    /// </summary>
    public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) =>
        Task.FromException<IList<IBatchContainer>>(CreatePipelineUnavailableException());

    /// <inheritdoc />
    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) =>
        Task.FromException(CreatePipelineUnavailableException());

    private InvalidOperationException CreatePipelineUnavailableException() =>
        new($"The ADO.NET retained-log receiver for provider '{_providerId}', queue '{_queueId}' requires the shared recoverable stream cache and receiver pipeline.");
}
