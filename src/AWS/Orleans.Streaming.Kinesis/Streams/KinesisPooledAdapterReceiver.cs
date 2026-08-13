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
    private readonly IStreamQueueCheckpointerFactory _checkpointerFactory;
    private readonly string _partition;
    private readonly KinesisRecoverableStreamSource _source;
    private readonly KinesisRecoverableStreamDataAdapter _dataAdapter;
    private readonly RecoverableStreamQueueCache<KinesisCacheRecord> _cache;
    private readonly Action<KinesisPooledAdapterReceiver>? _onShutdown;
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _lifecycleCancellation = new();
    private RecoverableStreamReceiver<KinesisCacheRecord>? _inner;
    private Task? _initializationTask;
    private int _initialized;
    private int _shutdown;

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
        Action<KinesisPooledAdapterReceiver>? onShutdown = null)
    {
        _checkpointerFactory = checkpointerFactory;
        _partition = partition;
        _onShutdown = onShutdown;
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
            _onShutdown?.Invoke(this);
        }
    }

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
        => _inner?.UpdateDeliveryProgress(earliestSubscriptionToken, utcNow);

    private Task EnsureInitialized(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _shutdown) != 0 || Volatile.Read(ref _initialized) != 0)
            {
                return Task.CompletedTask;
            }

            if (_initializationTask is null || _initializationTask.IsCompleted)
            {
                _initializationTask = InitializeCore();
            }

            return _initializationTask.WaitAsync(cancellationToken);
        }
    }

    private async Task InitializeCore()
    {
        var lifecycleToken = _lifecycleCancellation.Token;
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
                startFromNow: false);
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
