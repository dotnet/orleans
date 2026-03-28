using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Orleans.Streaming.Redis.Streams;

public class RedisStreamAdapter : IQueueAdapter
{
    private readonly string providerName;
    private readonly RedisStreamOptions redisStreamOptions;
    private readonly RedisStreamReceiverOptions redisStreamReceiverOptions;

    private readonly ClusterOptions clusterOptions;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter;
    private readonly IStreamQueueMapper streamQueueMapper;
    private readonly ILoggerFactory loggerFactory;

    private readonly SemaphoreSlim semaphoreSlim = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<QueueId, RedisStreamStorage> StreamStorages = new();

    internal RedisStreamAdapter(string providerName,
        ClusterOptions clusterOptions,
        RedisStreamOptions redisStreamOptions,
        RedisStreamReceiverOptions redisStreamReceiverOptions,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        IStreamQueueMapper streamQueueMapper,
        ILoggerFactory loggerFactory)
    {
        this.providerName = providerName;
        this.clusterOptions = clusterOptions;
        this.redisStreamOptions = redisStreamOptions;
        this.redisStreamReceiverOptions = redisStreamReceiverOptions;

        this.dataAdapter = dataAdapter;
        this.streamQueueMapper = streamQueueMapper;
        this.loggerFactory = loggerFactory;
    }

    public string Name => providerName;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var streamStorage = CreateStreamStorage(queueId);
        return RedisStreamAdapterReceiver.Create(queueId, dataAdapter, streamStorage, loggerFactory);
    }

    private ValueTask<RedisStreamStorage> GetOrCreateStreamStorageAsync(QueueId queueId)
    {
        if (StreamStorages.TryGetValue(queueId, out var queue))
        {
            return ValueTask.FromResult(queue);
        }
        return GetStreamStorageAsync(queueId);
    }

    private async ValueTask<RedisStreamStorage> GetStreamStorageAsync(QueueId queueId)
    {
        await semaphoreSlim.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!StreamStorages.TryGetValue(queueId, out var streamStorage))
            {
                streamStorage = CreateStreamStorage(queueId);
                await streamStorage.InitializeAsync();
                StreamStorages[queueId] = streamStorage;
            }
            return streamStorage;
        }
        finally
        {
            semaphoreSlim.Release();
        }
    }

    private RedisStreamStorage CreateStreamStorage(QueueId queueId)
    {
        var streamStorage = new RedisStreamStorage(queueId, clusterOptions, redisStreamOptions, redisStreamReceiverOptions, loggerFactory);
        return streamStorage;
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var queueId = streamQueueMapper.GetQueueForStream(streamId);

        var streamStorage = await GetOrCreateStreamStorageAsync(queueId);

        var streamEntry = dataAdapter
            .ToQueueMessage(streamId, events, token, requestContext);

        await streamStorage
            .AddEntryAsync(streamEntry);
    }
}
