using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamAdapter : IQueueAdapter
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Serializer<RedisStreamBatchContainer> _serializer;
    private readonly ClusterOptions _clusterOptions;
    private readonly RedisStreamingOptions _redisOptions;
    private readonly RedisStreamReceiverOptions _receiverOptions;
    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<QueueId, RedisStreamStorage> _queues = new();

    public RedisStreamAdapter(
        ILoggerFactory loggerFactory,
        Serializer<RedisStreamBatchContainer> serializer,
        string providerName,
        ClusterOptions clusterOptions,
        RedisStreamingOptions redisOptions,
        RedisStreamReceiverOptions receiverOptions,
        HashRingBasedStreamQueueMapper streamQueueMapper)
    {
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        Name = providerName;
        _clusterOptions = clusterOptions;
        _redisOptions = redisOptions;
        _receiverOptions = receiverOptions;
        _streamQueueMapper = streamQueueMapper;
    }

    public string Name { get; }

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var queue = new RedisStreamStorage(_loggerFactory, _clusterOptions, _redisOptions, _receiverOptions, queueId);
        var receiver = new RedisStreamAdapterReceiver(_loggerFactory, _serializer, queue);
        return receiver;
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        if (token is not null)
        {
            throw new ArgumentException("RedisStream stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
        }

        var queueId = _streamQueueMapper.GetQueueForStream(streamId);
        var queue = await GetStorage(queueId);
        var payload = RedisStreamBatchContainer.ToRedisValue(_serializer, streamId, events, requestContext);
        await queue.AddMessageAsync(payload);
    }

    private ValueTask<RedisStreamStorage> GetStorage(QueueId queueId)
    {
        if (_queues.TryGetValue(queueId, out var queue))
        {
            return ValueTask.FromResult(queue);
        }
        return GetStorageAsync(queueId);
    }

    private async ValueTask<RedisStreamStorage> GetStorageAsync(QueueId queueId)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_queues.TryGetValue(queueId, out var queue))
            {
                queue = new RedisStreamStorage(_loggerFactory, _clusterOptions, _redisOptions, _receiverOptions, queueId);
                await queue.ConnectAsync();
                _queues[queueId] = queue;
            }
            return queue;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
