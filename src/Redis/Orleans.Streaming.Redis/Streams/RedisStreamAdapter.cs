using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Orleans.Streaming.Redis.Streams;

public class RedisStreamAdapter : IQueueAdapter
{
    private readonly string _providerName;
    private readonly RedisStreamOptions _redisStreamOptions;
    private readonly RedisStreamReceiverOptions _redisStreamReceiverOptions;

    private readonly ClusterOptions _clusterOptions;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> _dataAdapter;
    private readonly IStreamQueueMapper _streamQueueMapper;
    private readonly ILoggerFactory _loggerFactory;

    private readonly SemaphoreSlim _semaphoreSlim = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<QueueId, RedisStreamStorage> _streamStorages = new();

    internal RedisStreamAdapter(string providerName,
        ClusterOptions clusterOptions,
        RedisStreamOptions redisStreamOptions,
        RedisStreamReceiverOptions redisStreamReceiverOptions,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        IStreamQueueMapper streamQueueMapper,
        ILoggerFactory loggerFactory)
    {
        _providerName = providerName;
        _clusterOptions = clusterOptions;
        _redisStreamOptions = redisStreamOptions;
        _redisStreamReceiverOptions = redisStreamReceiverOptions;

        _dataAdapter = dataAdapter;
        _streamQueueMapper = streamQueueMapper;
        _loggerFactory = loggerFactory;
    }

    public string Name => _providerName;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var streamStorage = CreateStreamStorage(queueId);
        return RedisStreamAdapterReceiver.Create(queueId, _dataAdapter, streamStorage, _loggerFactory);
    }

    private ValueTask<RedisStreamStorage> GetOrCreateStreamStorageAsync(QueueId queueId)
    {
        if (_streamStorages.TryGetValue(queueId, out var queue))
        {
            return ValueTask.FromResult(queue);
        }
        return GetStreamStorageAsync(queueId);
    }

    private async ValueTask<RedisStreamStorage> GetStreamStorageAsync(QueueId queueId)
    {
        await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_streamStorages.TryGetValue(queueId, out var streamStorage))
            {
                streamStorage = CreateStreamStorage(queueId);
                await streamStorage.InitializeAsync();
                _streamStorages[queueId] = streamStorage;
            }
            return streamStorage;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    private RedisStreamStorage CreateStreamStorage(QueueId queueId)
    {
        var streamStorage = new RedisStreamStorage(queueId, _clusterOptions, _redisStreamOptions, _redisStreamReceiverOptions, _loggerFactory);
        return streamStorage;
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var queueId = _streamQueueMapper.GetQueueForStream(streamId);

        var streamStorage = await GetOrCreateStreamStorageAsync(queueId);

        var streamEntry = _dataAdapter
            .ToQueueMessage(streamId, events, token, requestContext);

        await streamStorage
            .AddEntryAsync(streamEntry);
    }
}
