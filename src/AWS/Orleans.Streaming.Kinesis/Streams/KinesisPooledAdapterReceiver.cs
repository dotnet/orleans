using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
    private static readonly TimeSpan ReplayMetadataRetention = TimeSpan.FromDays(3650);
    private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
    private readonly string _partition;
    private readonly KinesisRecoverableStreamSource _source;
    private readonly KinesisRecoverableStreamDataAdapter _dataAdapter;
    private readonly RecoverableStreamQueueCache<KinesisCacheRecord> _cache;
    private readonly KinesisReplaySourceFactory _replaySourceFactory;
    private readonly Func<IRecoverableStreamQueueCache<KinesisCacheRecord>> _replayCacheFactory;
    private readonly RecoverableStreamReplayOptions _replayOptions;
    private readonly Action<KinesisPooledAdapterReceiver>? _onShutdown;
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _lifecycleCancellation = new();
    private RecoverableStreamReceiver<KinesisCacheRecord>? _inner;
    private Task? _initializationTask;
    private CancellationToken _initializationOwnerToken;
    private int _initialized;
    private int _shutdown;

    internal CancellationToken LifecycleCancellationToken => _lifecycleCancellation.Token;

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
        TimeProvider timeProvider,
        RecoverableStreamReplayOptions? replayOptions = null,
        Action<KinesisPooledAdapterReceiver>? onShutdown = null)
        : this(
            () => client,
            streamName,
            partition,
            checkpointerFactory,
            cacheOptions,
            serializer,
            loggerFactory,
            topologyMonitor,
            getRecordsInterval,
            timeProvider,
            replayOptions,
            onShutdown)
    {
    }

    public KinesisPooledAdapterReceiver(
        Func<IAmazonKinesis> clientFactory,
        string streamName,
        string partition,
        IStreamQueueCheckpointerFactory checkpointerFactory,
        SimpleQueueCacheOptions cacheOptions,
        Serializer<KinesisBatchContainer.Body> serializer,
        ILoggerFactory loggerFactory,
        KinesisShardTopologyMonitor topologyMonitor,
        TimeSpan getRecordsInterval,
        TimeProvider timeProvider,
        RecoverableStreamReplayOptions? replayOptions = null,
        Action<KinesisPooledAdapterReceiver>? onShutdown = null)
    {
        _checkpointerFactory = checkpointerFactory;
        _partition = partition;
        _onShutdown = onShutdown;
        _replayOptions = replayOptions ?? new RecoverableStreamReplayOptions();
        var logger = loggerFactory.CreateLogger<KinesisPooledAdapterReceiver>();
        var readThrottle = new KinesisShardReadThrottle(getRecordsInterval, timeProvider);
        _source = new(
            clientFactory(),
            streamName,
            partition,
            topologyMonitor,
            readThrottle);
        _dataAdapter = new(streamName, partition, serializer);
        _cache = CreateCache(cacheOptions.CacheSize, trackPurgedMetadata: false);
        _replayCacheFactory = () => CreateCache(_replayOptions.CacheSize, trackPurgedMetadata: true);
        _replaySourceFactory = new KinesisReplaySourceFactory(
            clientFactory,
            streamName,
            partition,
            topologyMonitor,
            readThrottle);

        RecoverableStreamQueueCache<KinesisCacheRecord> CreateCache(
            int cacheSize,
            bool trackPurgedMetadata)
        {
            var evictionStrategy = new ChronologicalEvictionStrategy(
                logger,
                new TimePurgePredicate(TimeSpan.MaxValue, TimeSpan.MaxValue),
                cacheMonitor: null,
                monitorWriteInterval: null);
            return new(
                Math.Min(1000, cacheSize),
                new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(BufferSize)),
                _dataAdapter,
                evictionStrategy,
                logger,
                metadataMinTimeInCache: trackPurgedMetadata ? ReplayMetadataRetention : null,
                maxCacheSize: cacheSize);
        }
    }

    public async Task Initialize(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await EnsureInitialized(cancellation.Token);
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        => await GetQueueMessagesAsync(maxCount, CancellationToken.None);

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdown) != 0 || maxCount <= 0)
        {
            return [];
        }

        await EnsureInitialized(cancellationToken);
        if (Volatile.Read(ref _shutdown) != 0)
        {
            return [];
        }

        return await GetInner().GetQueueMessagesAsync(maxCount, cancellationToken);
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        => Task.CompletedTask;

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public async Task Shutdown(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        try
        {
            _lifecycleCancellation.Cancel();
            var shutdownWatch = Stopwatch.StartNew();
            using var cancellation = timeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(timeout);
            var cancellationToken = cancellation?.Token ?? CancellationToken.None;
            List<Exception>? exceptions = null;
            Task? initializationTask;
            lock (_lifecycleLock)
            {
                initializationTask = _initializationTask;
            }

            if (initializationTask is not null)
            {
                try
                {
                    await initializationTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                    when (_lifecycleCancellation.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    (exceptions ??= []).Add(exception);
                }
            }

            try
            {
                if (_inner is null)
                {
                    await _source.Shutdown(cancellationToken);
                }
                else
                {
                    var remaining = timeout == Timeout.InfiniteTimeSpan
                        ? Timeout.InfiniteTimeSpan
                        : timeout > shutdownWatch.Elapsed
                            ? timeout - shutdownWatch.Elapsed
                            : TimeSpan.Zero;
                    await _inner.Shutdown(remaining);
                }
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            if (exceptions is [var singleException])
            {
                ExceptionDispatchInfo.Capture(singleException).Throw();
            }

            if (exceptions is { Count: > 1 })
            {
                throw new AggregateException(exceptions);
            }
        }
        finally
        {
            _lifecycleCancellation.Dispose();
            _onShutdown?.Invoke(this);
        }
    }

    public int GetMaxAddCount() => _inner?.GetMaxAddCount() ?? _cache.GetMaxAddCount();

    public void AddToCache(IList<IBatchContainer> messages)
    {
    }

    public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
        => _inner?.TryPurgeFromCache(out purgedItems) ?? _cache.TryPurgeFromCache(out purgedItems);

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        => _inner?.GetCacheCursor(streamId, token) ?? _cache.GetCacheCursor(streamId, token);

    public IQueueCacheCursor GetCacheCursorAtPosition(
        StreamId streamId,
        StreamSubscriptionStartPosition startPosition)
        => _cache.GetCacheCursorAtPosition(streamId, startPosition);

    public bool IsUnderPressure() => _inner?.IsUnderPressure() ?? _cache.IsUnderPressure();

    public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        => _inner?.UpdateDeliveryProgress(earliestSubscriptionToken, utcNow);

    private async Task EnsureInitialized(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task initializationTask;
            CancellationToken initializationOwnerToken;
            lock (_lifecycleLock)
            {
                if (Volatile.Read(ref _shutdown) != 0 || Volatile.Read(ref _initialized) != 0)
                {
                    return;
                }

                if (_initializationTask is null || _initializationTask.IsCompleted)
                {
                    _initializationOwnerToken = cancellationToken;
                    _initializationTask = InitializeCore(cancellationToken);
                }

                initializationTask = _initializationTask;
                initializationOwnerToken = _initializationOwnerToken;
            }

            try
            {
                await initializationTask.WaitAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && initializationOwnerToken.IsCancellationRequested
                && initializationTask.IsCanceled)
            {
                // The caller which owned the shared initialization attempt canceled.
                // An unaffected caller retries after that attempt has settled.
            }
        }
    }

    private async Task InitializeCore(CancellationToken initializationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifecycleCancellation.Token,
            initializationToken);
        var lifecycleToken = cancellation.Token;
        if (_inner is null)
        {
            var checkpointer = await _checkpointerFactory.Create(_partition, lifecycleToken);
            if (Volatile.Read(ref _shutdown) != 0)
            {
                return;
            }

            _inner = new(
                _source,
                _dataAdapter,
                _cache,
                checkpointer,
                startFromNow: false,
                _replaySourceFactory,
                _replayCacheFactory,
                _replayOptions);
        }

        await _inner.Initialize(lifecycleToken);
        if (Volatile.Read(ref _shutdown) == 0)
        {
            Volatile.Write(ref _initialized, 1);
        }
    }

    private RecoverableStreamReceiver<KinesisCacheRecord> GetInner()
        => _inner ?? throw new InvalidOperationException("The Kinesis receiver has not been initialized.");
}
