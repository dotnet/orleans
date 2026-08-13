using System.Diagnostics.CodeAnalysis;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Receives message batches from an individual retained-log partition.
/// </summary>
internal sealed class AdoNetQueueAdapterReceiver : IQueueAdapterReceiver, IQueueCache
{
    private const int BufferSize = 1024 * 1024;
    private readonly RecoverableStreamReceiver<AdoNetStreamMessage> _inner;

    internal Action<AdoNetQueueAdapterReceiver>? OnShutdown { get; set; }

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
        var source = new AdoNetRecoverableStream(
            clusterOptions.ServiceId,
            providerId,
            queueId,
            streamOptions,
            queries,
            logger);
        var checkpointer = new StreamQueueCheckpointer(
            source,
            new StreamQueueCheckpointerOptions
            {
                CheckpointComparer = StreamCheckpointComparers.Numeric,
                PersistInterval = streamOptions.CheckpointPersistInterval,
            });
        var dataAdapter = new AdoNetRecoverableStreamDataAdapter(serializer);
        var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(BufferSize));
        var evictionStrategy = new ChronologicalEvictionStrategy(
            logger,
            new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
            cacheMonitor: null,
            monitorWriteInterval: null);
        var cache = new RecoverableStreamQueueCache<AdoNetStreamMessage>(
            Math.Min(streamOptions.MaxMessagesPerRead, cacheOptions.CacheSize),
            bufferPool,
            dataAdapter,
            evictionStrategy,
            logger,
            maxCacheSize: cacheOptions.CacheSize);
        _inner = new RecoverableStreamReceiver<AdoNetStreamMessage>(
            source,
            dataAdapter,
            cache,
            checkpointer,
            streamOptions.StartFromNow);
    }

    public Task Initialize(TimeSpan timeout) => _inner.Initialize(timeout);

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            await _inner.Shutdown(timeout);
        }
        finally
        {
            OnShutdown?.Invoke(this);
        }
    }

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        => _inner.GetQueueMessagesAsync(maxCount, CancellationToken.None);

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(
        int maxCount,
        CancellationToken cancellationToken)
        => _inner.GetQueueMessagesAsync(maxCount, cancellationToken);

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        => _inner.MessagesDeliveredAsync(messages, CancellationToken.None);

    public Task MessagesDeliveredAsync(
        IList<IBatchContainer> messages,
        CancellationToken cancellationToken)
        => _inner.MessagesDeliveredAsync(messages, cancellationToken);

    public int GetMaxAddCount() => _inner.GetMaxAddCount();

    public void AddToCache(IList<IBatchContainer> messages) => _inner.AddToCache(messages);

    public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
        => _inner.TryPurgeFromCache(out purgedItems);

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        => _inner.GetCacheCursor(streamId, token);

    public bool IsUnderPressure() => _inner.IsUnderPressure();

    public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        => _inner.UpdateDeliveryProgress(earliestSubscriptionToken, utcNow);
}
