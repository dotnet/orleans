using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.SQS.Streams;

/// <summary>
/// Adapter for simple queue caches.
/// </summary>
public class StreamIdPartitionedQueueAdapterCache : IQueueAdapterCache
{
    /// <summary>
    /// Cache size property name for configuration
    /// </summary>
    public const string CacheSizePropertyName = "CacheSize";

    private readonly int cacheSize;
    private readonly string providerName;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>
    /// Adapter for simple queue caches.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="providerName">The stream provider name.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public StreamIdPartitionedQueueAdapterCache(SimpleQueueCacheOptions options, string providerName, ILoggerFactory loggerFactory)
    {
        this.cacheSize = options.CacheSize;
        this.loggerFactory = loggerFactory;
        this.providerName = providerName;
    }

    /// <inheritdoc />
    public IQueueCache CreateQueueCache(QueueId queueId)
    {
        return new StreamIdPartitionedQueueCache(cacheSize, this.loggerFactory.CreateLogger($"{typeof(SimpleQueueCache).FullName}.{providerName}.{queueId}"));
    }
}

public class StreamIdPartitionedQueueCache : IQueueCache
{
    private Dictionary<StreamId, IQueueCache> _partitionedCaches = new();

    private ILogger logger;
    private int maxCacheSize;
    private readonly int CACHE_HISTOGRAM_MAX_BUCKET_SIZE = 10;

    public StreamIdPartitionedQueueCache(int cacheSize, ILogger logger)
    {
        maxCacheSize = cacheSize;
        this.logger = logger;
    }

    public int GetMaxAddCount() => CACHE_HISTOGRAM_MAX_BUCKET_SIZE;

    public void AddToCache(IList<IBatchContainer> messages)
    {
        foreach (var messagesByStream in messages.GroupBy(x => x.StreamId))
        {
            GetPartitionedCache(messagesByStream.Key)
                .AddToCache(messagesByStream.ToList());
        }
    }

    public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
    {
        bool purged = false;
        var collector = new List<IBatchContainer>();
        foreach (var cache in _partitionedCaches.Values)
        {
            if (cache.TryPurgeFromCache(out var partitionedPurgedItems))
            {
                purged = true;
                collector.AddRange(partitionedPurgedItems);
            }
        }

        purgedItems = collector;
        return purged;
    }

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token)
        => GetPartitionedCache(streamId).GetCacheCursor(streamId, token);

    public bool IsUnderPressure() =>
        _partitionedCaches.Values.Any(cache => cache.IsUnderPressure());

    private IQueueCache GetPartitionedCache(StreamId streamId)
    {
        if (!_partitionedCaches.TryGetValue(streamId, out var cache))
        {
            cache = new SimpleQueueCache(maxCacheSize, logger);
            _partitionedCaches.Add(streamId, cache);
        }
        return cache;
    }
}
