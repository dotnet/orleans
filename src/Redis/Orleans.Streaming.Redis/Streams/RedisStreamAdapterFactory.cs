using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

public class RedisStreamAdapterFactory : IQueueAdapterFactory
{
    private readonly RedisStreamServiceProvider provider;
    private readonly RedisStreamOptions options;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter;
    private readonly IStreamFailureHandler streamFailureHandler;
    private readonly IStreamQueueMapper streamQueueMapper;
    private readonly ILoggerFactory loggerFactory;

    public static IQueueAdapterFactory Create(IServiceProvider serviceProvider, string providerName)
    {
        var redisStreamServiceProvider = new RedisStreamServiceProvider(serviceProvider, providerName);
        var redisStreamAdapterFactory = new RedisStreamAdapterFactory(redisStreamServiceProvider);
        return redisStreamAdapterFactory;
    }

    public static IServiceCollection PostConfigureDefaults(IServiceCollection services, string providerName)
    {
        services.
            TryAddKeyedSingleton<IQueueDataAdapter<StreamEntry, IBatchContainer>, RedisStreamDataAdapter>(providerName);

        return services;
    }

    private RedisStreamAdapterFactory(RedisStreamServiceProvider provider)
    {
        this.provider = provider;

        options = provider.GetOptions<RedisStreamOptions>();

        dataAdapter = provider.GetComponentService<IQueueDataAdapter<StreamEntry, IBatchContainer>>();

        loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        streamFailureHandler = new RedisStreamFailureHandler(loggerFactory.CreateLogger<RedisStreamFailureHandler>());

        var hashRingStreamQueueMapperOptions = provider.GetOptions<HashRingStreamQueueMapperOptions>();
        streamQueueMapper = new HashRingBasedStreamQueueMapper(hashRingStreamQueueMapperOptions, provider.Name);
    }

    public async Task<IQueueAdapter> CreateAdapter()
    {
        var connectionMultiplexer = await options.CreateMultiplexer(options);
        var clusterOptions = provider.GetRequiredService<IOptions<ClusterOptions>>().Value;

        var queueAdapter = new RedisStreamAdapter(provider, options, clusterOptions,
            dataAdapter, connectionMultiplexer, streamQueueMapper, loggerFactory);

        return queueAdapter;
    }

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        return Task.FromResult(streamFailureHandler);
    }

    public IQueueAdapterCache GetQueueAdapterCache()
    {
        var simpleQueueCacheOptions = provider.GetOptions<SimpleQueueCacheOptions>();
        return new SimpleQueueAdapterCache(simpleQueueCacheOptions, provider.Name, loggerFactory);
    }

    public IStreamQueueMapper GetStreamQueueMapper()
    {
        return streamQueueMapper;
    }
}
