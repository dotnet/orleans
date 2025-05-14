using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamAdapterFactory : IQueueAdapterFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Serializer<RedisStreamBatchContainer> _serializer;
    private readonly string _providerName;
    private readonly ClusterOptions _clusterOptions;
    private readonly RedisStreamingOptions _redisOptions;
    private readonly RedisStreamReceiverOptions _receiverOptions;
    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SimpleQueueAdapterCache _queueAdapterCache;

    public RedisStreamAdapterFactory(
        ILoggerFactory loggerFactory,
        Serializer<RedisStreamBatchContainer> serializer,
        string providerName,
        ClusterOptions clusterOptions,
        HashRingStreamQueueMapperOptions streamQueueMapperOptions,
        SimpleQueueCacheOptions queueCacheOptions,
        RedisStreamingOptions redisOptions,
        RedisStreamReceiverOptions receiverOptions)
    {
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        _providerName = providerName;
        _clusterOptions = clusterOptions;
        _redisOptions = redisOptions;
        _receiverOptions = receiverOptions;
        _streamQueueMapper = new HashRingBasedStreamQueueMapper(streamQueueMapperOptions, providerName);
        _queueAdapterCache = new SimpleQueueAdapterCache(queueCacheOptions, providerName, loggerFactory);
    }

    public Task<IQueueAdapter> CreateAdapter()
    {
        var adapter = new RedisStreamAdapter(_loggerFactory, _serializer, _providerName, _clusterOptions, _redisOptions, _receiverOptions, _streamQueueMapper);
        return Task.FromResult<IQueueAdapter>(adapter);
    }

    public IQueueAdapterCache GetQueueAdapterCache() => _queueAdapterCache;

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());

    public static RedisStreamAdapterFactory Create(IServiceProvider services, string name)
    {
        var clusterOptions = services.GetProviderClusterOptions(name).Value;
        var streamQueueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        var queueCacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var redisOptions = services.GetOptionsByName<RedisStreamingOptions>(name);
        var receiverOptions = services.GetOptionsByName<RedisStreamReceiverOptions>(name);
        return ActivatorUtilities.CreateInstance<RedisStreamAdapterFactory>(services, name, clusterOptions, redisOptions, receiverOptions, streamQueueMapperOptions, queueCacheOptions);
    }
}
