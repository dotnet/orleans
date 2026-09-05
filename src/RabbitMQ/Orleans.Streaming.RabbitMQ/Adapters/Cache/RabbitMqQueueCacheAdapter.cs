using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters.Cache;

internal class RabbitMqQueueCacheAdapter : IQueueAdapterCache
{
    private readonly RabbitMQQueueCacheOptions _rabbitMqQueueCacheOptions;

    public RabbitMqQueueCacheAdapter(RabbitMQQueueCacheOptions rabbitMqQueueCacheOptions)
    {
        _rabbitMqQueueCacheOptions = rabbitMqQueueCacheOptions;
    }

    public IQueueCache CreateQueueCache(QueueId queueId) => new RabbitMqQueueCache(_rabbitMqQueueCacheOptions);
}