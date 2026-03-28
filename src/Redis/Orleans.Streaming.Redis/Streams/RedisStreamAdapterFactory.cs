using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

public class RedisStreamAdapterFactory : IQueueAdapterFactory
{
    private readonly string _providerName;
    private readonly ClusterOptions _clusterOptions;
    private readonly RedisStreamOptions _redisStreamOptions;
    private readonly RedisStreamReceiverOptions _redisStreamReceiverOptions;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> _queueDataAdapter;
    private readonly IStreamQueueMapper _streamQueueMapper;
    private readonly IQueueAdapterCache _queueAdapterCache;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Application level failure handler override.
    /// </summary>
    protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

    public static RedisStreamAdapterFactory Create(IServiceProvider serviceProvider, string providerName)
    {
        var clusterOptions = serviceProvider.GetProviderClusterOptions(providerName).Value;
        var redisStreamOptions = serviceProvider.GetOptionsByName<RedisStreamOptions>(providerName);
        var redisStreamReceiverOptions = serviceProvider.GetOptionsByName<RedisStreamReceiverOptions>(providerName);
        var hashRingStreamQueueMapperOptions = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(providerName);
        var simpleQueueCacheOptions = serviceProvider.GetOptionsByName<SimpleQueueCacheOptions>(providerName);
        var queueDataAdapter = serviceProvider.GetRequiredKeyedService<IQueueDataAdapter<StreamEntry, IBatchContainer>>(providerName);

        var factory = ActivatorUtilities
            .CreateInstance<RedisStreamAdapterFactory>(serviceProvider,
                providerName, clusterOptions, redisStreamOptions, redisStreamReceiverOptions,
                hashRingStreamQueueMapperOptions, simpleQueueCacheOptions,
                queueDataAdapter);
        factory.Init();
        return factory;
    }

    public static IServiceCollection PostConfigureDefaults(IServiceCollection services, string providerName)
    {
        services.
            TryAddKeyedSingleton<IQueueDataAdapter<StreamEntry, IBatchContainer>, RedisStreamDataAdapter>(providerName);

        return services;
    }

    internal RedisStreamAdapterFactory() { }

    public RedisStreamAdapterFactory(
        string providerName,
        ClusterOptions clusterOptions,
        RedisStreamOptions redisStreamOptions,
        RedisStreamReceiverOptions redisStreamReceiverOptions,
        HashRingStreamQueueMapperOptions hashRingStreamQueueMapperOptions,
        SimpleQueueCacheOptions simpleQueueCacheOptions,
        IQueueDataAdapter<StreamEntry, IBatchContainer> queueDataAdapter,
        ILoggerFactory loggerFactory)
    {
        _providerName = providerName;
        _clusterOptions = clusterOptions;

        _redisStreamOptions = redisStreamOptions;
        _redisStreamReceiverOptions = redisStreamReceiverOptions;
        _queueDataAdapter = queueDataAdapter;
        _loggerFactory = loggerFactory;

        _streamQueueMapper = new HashRingBasedStreamQueueMapper(hashRingStreamQueueMapperOptions, providerName);
        _queueAdapterCache = new SimpleQueueAdapterCache(simpleQueueCacheOptions, providerName, loggerFactory);
    }

    /// <summary> Init the factory.</summary>
    public virtual void Init()
    {
        StreamFailureHandlerFactory ??=
                qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
    }

    public Task<IQueueAdapter> CreateAdapter()
    {
        var queueAdapter = new RedisStreamAdapter(_providerName, _clusterOptions, _redisStreamOptions,
            _redisStreamReceiverOptions, _queueDataAdapter, _streamQueueMapper, _loggerFactory);

        return Task.FromResult<IQueueAdapter>(queueAdapter);
    }

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
           StreamFailureHandlerFactory(queueId);

    public IQueueAdapterCache GetQueueAdapterCache()
    {
        return _queueAdapterCache;
    }

    public IStreamQueueMapper GetStreamQueueMapper()
    {
        return _streamQueueMapper;
    }
}
