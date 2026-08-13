using System.Diagnostics.CodeAnalysis;
using Amazon.Kinesis;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis;

internal sealed class KinesisPooledAdapterReceiver : IQueueAdapterReceiver, IQueueCache
{
    private const int BufferSize = 1024 * 1024;
    private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
    private readonly string _partition;
    private readonly KinesisRecoverableStreamSource _source;
    private readonly KinesisRecoverableStreamDataAdapter _dataAdapter;
    private readonly RecoverableStreamQueueCache<KinesisCacheRecord> _cache;
    private RecoverableStreamReceiver<KinesisCacheRecord>? _inner;

    public KinesisPooledAdapterReceiver(
        IAmazonKinesis client,
        string streamName,
        string partition,
        IStreamQueueCheckpointerFactory checkpointerFactory,
        SimpleQueueCacheOptions cacheOptions,
        Serializer<KinesisBatchContainer.Body> serializer,
        ILoggerFactory loggerFactory,
        KinesisShardTopologyMonitor topologyMonitor,
        TimeSpan getRecordsInterval,
        TimeProvider timeProvider)
    {
        _checkpointerFactory = checkpointerFactory;
        _partition = partition;
        var logger = loggerFactory.CreateLogger<KinesisPooledAdapterReceiver>();
        _source = new(
            client,
            streamName,
            partition,
            topologyMonitor,
            getRecordsInterval,
            timeProvider);
        _dataAdapter = new(serializer);
        var evictionStrategy = new ChronologicalEvictionStrategy(
            logger,
            new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
            cacheMonitor: null,
            monitorWriteInterval: null);
        _cache = new(
            Math.Min(1000, cacheOptions.CacheSize),
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(BufferSize)),
            _dataAdapter,
            evictionStrategy,
            logger,
            maxCacheSize: cacheOptions.CacheSize);
    }

    public async Task Initialize(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var checkpointer = await _checkpointerFactory.Create(_partition, cancellation.Token);
        _inner = new(
            _source,
            _dataAdapter,
            _cache,
            checkpointer,
            startFromNow: false);
        await _inner.Initialize(timeout);
    }

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        => GetInner().GetQueueMessagesAsync(maxCount, CancellationToken.None);

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount, CancellationToken cancellationToken)
        => GetInner().GetQueueMessagesAsync(maxCount, cancellationToken);

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        => Task.CompletedTask;

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public Task Shutdown(TimeSpan timeout)
        => _inner is null ? _source.Shutdown(CancellationToken.None) : _inner.Shutdown(timeout);

    public int GetMaxAddCount() => _cache.GetMaxAddCount();

    public void AddToCache(IList<IBatchContainer> messages)
    {
    }

    public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
        => _cache.TryPurgeFromCache(out purgedItems);

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        => _cache.GetCacheCursor(streamId, token);

    public bool IsUnderPressure() => _cache.IsUnderPressure();

    public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        => GetInner().UpdateDeliveryProgress(earliestSubscriptionToken, utcNow);

    private RecoverableStreamReceiver<KinesisCacheRecord> GetInner()
        => _inner ?? throw new InvalidOperationException("The Kinesis receiver has not been initialized.");
}
