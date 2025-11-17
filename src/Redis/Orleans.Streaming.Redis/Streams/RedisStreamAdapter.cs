using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streaming.Redis.Streams;

public class RedisStreamAdapter : IQueueAdapter
{
    private readonly RedisStreamServiceProvider provider;
    private readonly RedisStreamOptions options;

    private readonly ClusterOptions clusterOptions;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter;
    private readonly IConnectionMultiplexer connectionMultiplexer;
    private readonly IStreamQueueMapper streamQueueMapper;
    private readonly ILoggerFactory loggerFactory;

    private readonly ConcurrentDictionary<QueueId, RedisStreamStorage> StreamStorages = new();

    internal RedisStreamAdapter(RedisStreamServiceProvider provider,
        RedisStreamOptions options,
        ClusterOptions clusterOptions,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        IConnectionMultiplexer connectionMultiplexer,
        IStreamQueueMapper streamQueueMapper,
        ILoggerFactory loggerFactory)
    {
        this.provider = provider;
        this.options = options;
        this.clusterOptions = clusterOptions;
        this.dataAdapter = dataAdapter;
        this.connectionMultiplexer = connectionMultiplexer;
        this.streamQueueMapper = streamQueueMapper;
        this.loggerFactory = loggerFactory;
    }

    public string Name => provider.Name;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var storage = GetStorage(queueId);
        return RedisStreamAdapterReceiver.Create(options, dataAdapter, storage, queueId, TimeProvider.System, loggerFactory);
    }

    private RedisStreamStorage GetStorage(QueueId queueId)
    {
        var streamKey = options.GetRedisKey(clusterOptions, queueId);
        var storage = new RedisStreamStorage(connectionMultiplexer, streamKey, queueId.ToString(), loggerFactory);
        return storage;
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var queueId = streamQueueMapper.GetQueueForStream(streamId);

        if (!StreamStorages.TryGetValue(queueId, out var streamStorage))
        {
            var tmpStreamStorage = GetStorage(queueId);
            await tmpStreamStorage.InitAsync();
            streamStorage = StreamStorages.GetOrAdd(queueId, tmpStreamStorage);
        }

        var streamEntry = dataAdapter
            .ToQueueMessage(streamId, events, token, requestContext);

        await streamStorage
            .AddEntryAsync(streamEntry);
    }
}
