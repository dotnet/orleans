using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.NATS;

internal class NatsAdapterFactory : IQueueAdapterFactory
{
    private readonly string providerName;
    private readonly NatsOptions natsOptions;
    private readonly Serializer serializer;
    private readonly ILoggerFactory loggerFactory;
    private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
    private readonly IQueueAdapterCache adapterCache;

    /// <summary>
    /// Application level failure handler override.
    /// </summary>
    protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; } = default!;

    public NatsAdapterFactory(
        string name,
        NatsOptions natsOptions,
        HashRingStreamQueueMapperOptions queueMapperOptions,
        SimpleQueueCacheOptions cacheOptions,
        IOptions<ClusterOptions> clusterOptions,
        Serializer serializer,
        ILoggerFactory loggerFactory)
    {
        this.providerName = name;
        this.natsOptions = natsOptions;
        this.serializer = serializer;
        this.loggerFactory = loggerFactory;
        streamQueueMapper = new HashRingBasedStreamQueueMapper(queueMapperOptions, this.providerName);
        adapterCache = new SimpleQueueAdapterCache(cacheOptions, this.providerName, this.loggerFactory);
    }

    /// <summary> Init the factory.</summary>
    public virtual void Init()
    {
        if (StreamFailureHandlerFactory == null)
        {
            StreamFailureHandlerFactory =
                qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
        }
    }

    /// <summary>Creates the NATS based adapter.</summary>
    public virtual async Task<IQueueAdapter> CreateAdapter()
    {
        var connectionManager = new NatsConnectionManager(this.providerName, this.loggerFactory, this.natsOptions);
        await connectionManager.Initialize();

        var adapter = new NatsAdapter(this.providerName, this.natsOptions, this.loggerFactory, this.serializer, connectionManager);

        return adapter;
    }

    /// <summary>Creates the adapter cache.</summary>
    public virtual IQueueAdapterCache GetQueueAdapterCache()
    {
        return adapterCache;
    }

    /// <summary>Creates the factory stream queue mapper.</summary>
    public IStreamQueueMapper GetStreamQueueMapper()
    {
        return streamQueueMapper;
    }

    /// <summary>
    /// Creates a delivery failure handler for the specified queue.
    /// </summary>
    /// <param name="queueId"></param>
    /// <returns></returns>
    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        return StreamFailureHandlerFactory(queueId);
    }

    public static NatsAdapterFactory Create(IServiceProvider services, string name)
    {
        var natsOptions = services.GetOptionsByName<NatsOptions>(name);
        var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);
        var factory = ActivatorUtilities.CreateInstance<NatsAdapterFactory>(services, name, natsOptions, cacheOptions,
            queueMapperOptions, clusterOptions);
        factory.Init();
        return factory;
    }
}
