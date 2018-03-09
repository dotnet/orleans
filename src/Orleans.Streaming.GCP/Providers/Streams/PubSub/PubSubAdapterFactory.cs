﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Configuration;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    public class PubSubAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IPubSubDataAdapter
    {
        private readonly string _providerName;
        private readonly PubSubOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly ILoggerFactory loggerFactory;
        private readonly Func<TDataAdapter> _adaptorFactory;
        private HashRingBasedStreamQueueMapper _streamQueueMapper;
        private IQueueAdapterCache _adapterCache;

        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public PubSubAdapterFactory(
            string name, 
            PubSubOptions options, 
            HashRingStreamQueueMapperOptions queueMapperOptions,
            SimpleQueueCacheOptions cacheOptions,
            IServiceProvider serviceProvider, 
            IOptions<ClusterOptions> clusterOptions, 
            SerializationManager serializationManager, 
            ILoggerFactory loggerFactory)
        {
            this._providerName = name;
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.SerializationManager = serializationManager;
            this.loggerFactory = loggerFactory;
            this._adaptorFactory = () => ActivatorUtilities.GetServiceOrCreateInstance<TDataAdapter>(serviceProvider);
            this._streamQueueMapper = new HashRingBasedStreamQueueMapper(queueMapperOptions, this._providerName);
            this._adapterCache = new SimpleQueueAdapterCache(cacheOptions, this._providerName, loggerFactory);
        }

        public virtual void Init()
        {
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new PubSubAdapter<TDataAdapter>(_adaptorFactory(), SerializationManager, this.loggerFactory, _streamQueueMapper,
                this.options.ProjectId, this.options.TopicId, this.options.ClusterId ?? this.clusterOptions.ClusterId, this._providerName, this.options.Deadline, this.options.CustomEndpoint);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => StreamFailureHandlerFactory(queueId);

        public IQueueAdapterCache GetQueueAdapterCache() => _adapterCache;

        public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

        public static PubSubAdapterFactory<TDataAdapter> Create(IServiceProvider services, string name)
        {
            var pubsubOptions = services.GetOptionsByName<PubSubOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
            var factory = ActivatorUtilities.CreateInstance<PubSubAdapterFactory<TDataAdapter>>(services, name, pubsubOptions, queueMapperOptions, cacheOptions);
            factory.Init();
            return factory;
        }
    }
}
