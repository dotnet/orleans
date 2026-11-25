using System;
using System.Collections.Generic;
using System.Linq;
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

internal class NatsAdapterFactory : IQueueAdapterFactory, IAsyncDisposable
{
    private readonly string providerName;
    private readonly NatsOptions natsOptions;
    private readonly Serializer serializer;
    private readonly ILoggerFactory loggerFactory;
    private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
    private readonly IQueueAdapterCache adapterCache;
    private readonly HashSet<NatsConnectionManager> connectionManagers = [];
    private Task? disposalTask;

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
        NatsConnectionManager connectionManager;
        lock (connectionManagers)
        {
            ObjectDisposedException.ThrowIf(disposalTask is not null, this);
            connectionManager = new NatsConnectionManager(this.providerName, this.loggerFactory, this.natsOptions);
            connectionManagers.Add(connectionManager);
        }

        try
        {
            await connectionManager.Initialize();
            lock (connectionManagers)
            {
                ObjectDisposedException.ThrowIf(disposalTask is not null, this);
                return new NatsAdapter(this.providerName, this.natsOptions, this.loggerFactory, this.serializer, connectionManager);
            }
        }
        catch
        {
            try
            {
                await connectionManager.DisposeAsync();
            }
            finally
            {
                lock (connectionManagers)
                {
                    connectionManagers.Remove(connectionManager);
                }
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (connectionManagers)
        {
            return new(disposalTask ??= Task.WhenAll(connectionManagers.Select(manager => manager.DisposeAsync().AsTask())));
        }
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
