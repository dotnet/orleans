using System.Diagnostics.CodeAnalysis;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Receives records from one stream partition through the recoverable stream partition pipeline.
/// </summary>
internal sealed class AdoNetQueueAdapterReceiver : IQueueAdapterReceiver, IQueueCache
{
    private const int BufferSize = 1024 * 1024;
    private readonly RecoverableStreamReceiver<AdoNetStreamMessage> _inner;
    private readonly AdoNetRecoverableStream _source;
    private int _shutdownNotified;

    internal Action<AdoNetQueueAdapterReceiver>? OnShutdown { get; set; }

    public AdoNetQueueAdapterReceiver(
        string providerId,
        string queueId,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        SimpleQueueCacheOptions cacheOptions,
        RelationalOrleansQueries queries,
        Serializer<AdoNetBatchContainer> serializer,
        ILogger<AdoNetQueueAdapterReceiver> logger,
        RecoverableStreamReplayOptions replayOptions,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        _source = new AdoNetRecoverableStream(
            clusterOptions.ServiceId,
            providerId,
            queueId,
            streamOptions,
            queries,
            logger,
            timeProvider);
        var checkpointer = new StreamQueueCheckpointer(
            _source,
            new StreamQueueCheckpointerOptions
            {
                CheckpointComparer = StreamCheckpointComparers.Numeric,
                PersistInterval = streamOptions.CheckpointPersistInterval,
            });
        var dataAdapter = new AdoNetRecoverableStreamDataAdapter(
            clusterOptions.ServiceId,
            providerId,
            queueId,
            serializer);
        var bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(BufferSize));
        var cache = CreateCache(cacheOptions.CacheSize, bufferPool);
        IRecoverableStreamQueueCache<AdoNetStreamMessage> CreateReplayCache()
            => CreateCache(
                replayOptions.CacheSize,
                new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(BufferSize)));
        _inner = new RecoverableStreamReceiver<AdoNetStreamMessage>(
            _source,
            dataAdapter,
            cache,
            checkpointer,
            streamOptions.StartFromNow,
            _source,
            CreateReplayCache,
            replayOptions);

        RecoverableStreamQueueCache<AdoNetStreamMessage> CreateCache(
            int cacheSize,
            IObjectPool<FixedSizeBuffer> pool)
        {
            var evictionStrategy = new ChronologicalEvictionStrategy(
                logger,
                new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
                cacheMonitor: null,
                monitorWriteInterval: null);
            return new(
                Math.Min(streamOptions.MaxMessagesPerRead, cacheSize),
                pool,
                dataAdapter,
                evictionStrategy,
                logger,
                maxCacheSize: cacheSize);
        }
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
            var acquisitionCompletion = _source.AcquisitionCompletion;
            if (acquisitionCompletion.IsCompleted)
            {
                NotifyShutdown();
            }
            else
            {
                _ = NotifyShutdownAfterAcquisition(acquisitionCompletion, NotifyShutdown);
            }
        }
    }

    internal static async Task NotifyShutdownAfterAcquisition(
        Task acquisitionCompletion,
        Action notifyShutdown)
    {
        ArgumentNullException.ThrowIfNull(acquisitionCompletion);
        ArgumentNullException.ThrowIfNull(notifyShutdown);
        await acquisitionCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        notifyShutdown();
    }

    private void NotifyShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownNotified, 1) == 0)
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

    public IQueueCacheCursor GetCacheCursorAtPosition(
        StreamId streamId,
        StreamSubscriptionStartPosition startPosition)
        => _inner.GetCacheCursorAtPosition(streamId, startPosition);

    public bool IsUnderPressure() => _inner.IsUnderPressure();

    public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        => _inner.UpdateDeliveryProgress(earliestSubscriptionToken, utcNow);
}
