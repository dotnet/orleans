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
using Orleans.Runtime;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class AzureQueueAdapterFactory : IQueueAdapterFactory
    {
        private readonly string providerName;
        private readonly AzureQueueOptions options;
        private readonly IQueueDataAdapter<CloudQueueMessage, IBatchContainer> dataAdapter;
        private readonly ClusterOptions clusterOptions;
        private readonly ILoggerFactory loggerFactory;
        private IAzureStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>

        protected SerializationManager SerializationManager { get; }

        public AzureQueueAdapterFactory(
            string name,
            AzureQueueOptions options, 
            SimpleQueueCacheOptions cacheOptions,
            IQueueDataAdapter<CloudQueueMessage, IBatchContainer> dataAdapter,
            IServiceProvider serviceProvider, 
            IOptions<ClusterOptions> clusterOptions, 
            SerializationManager serializationManager, 
            ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter)); ;
            this.clusterOptions = clusterOptions.Value;
            this.SerializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.streamQueueMapper = new AzureStreamQueueMapper(options.QueueNames, providerName);
            this.adapterCache = new SimpleQueueAdapterCache(cacheOptions, this.providerName, this.loggerFactory);
        }

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new AzureQueueAdapter(
                this.dataAdapter,
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

        public static AzureQueueAdapterFactory Create(IServiceProvider services, string name)
        {
            var azureQueueOptions = services.GetOptionsByName<AzureQueueOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var dataAdapter = services.GetServiceByName<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>(name)
                ?? services.GetService<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>();
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);
            var factory = ActivatorUtilities.CreateInstance<AzureQueueAdapterFactory>(services, name, azureQueueOptions, cacheOptions, dataAdapter, clusterOptions);
            return factory;
        }
    }
}
