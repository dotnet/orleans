using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class AzureQueueAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IAzureQueueDataAdapter
    {
        private readonly string providerName;
        private readonly AzureQueueOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly ILoggerFactory loggerFactory;
        private readonly Func<TDataAdapter> dataAadaptorFactory;
        private IAzureStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>

        protected SerializationManager SerializationManager { get; }

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public AzureQueueAdapterFactory(
            string name,
            AzureQueueOptions options, 
            SimpleQueueCacheOptions cacheOptions,
            IServiceProvider serviceProvider, 
            IOptions<ClusterOptions> clusterOptions, 
            SerializationManager serializationManager, 
            ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.clusterOptions = clusterOptions.Value;
            this.SerializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.dataAadaptorFactory = () => ActivatorUtilities.GetServiceOrCreateInstance<TDataAdapter>(serviceProvider);
            this.streamQueueMapper = new AzureStreamQueueMapper(options.QueueNames, providerName);
            this.adapterCache = new SimpleQueueAdapterCache(cacheOptions, this.providerName, this.loggerFactory);
        }

        /// <summary> Init the factory.</summary>
        public virtual void Init()
        {
            this.StreamFailureHandlerFactory = this.StreamFailureHandlerFactory ?? 
                    ((qid) => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler()));
        }

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new AzureQueueAdapter<TDataAdapter>(
                this.dataAadaptorFactory(), 
                this.SerializationManager, 
                this.streamQueueMapper, 
                this.loggerFactory, 
                this.options,
                this.clusterOptions.ServiceId, 
                this.providerName);
            return Task.FromResult<IQueueAdapter>(adapter);
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

        public static AzureQueueAdapterFactory<TDataAdapter> Create(IServiceProvider services, string name)
        {
            var azureQueueOptions = services.GetOptionsByName<AzureQueueOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);
            var factory = ActivatorUtilities.CreateInstance<AzureQueueAdapterFactory<TDataAdapter>>(services, name, azureQueueOptions, cacheOptions, clusterOptions);
            factory.Init();
            return factory;
        }
    }
}
