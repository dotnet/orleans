using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class AzureQueueAdapterFactory : IQueueAdapterFactory
    {
        private readonly string providerName;
        private readonly AzureQueueOptions options;
        private readonly IQueueDataAdapter<string, IBatchContainer> dataAdapter;
        private readonly ILoggerFactory loggerFactory;
        private readonly HashRingBasedPartitionedStreamQueueMapper streamQueueMapper;
        private readonly SimpleQueueAdapterCache adapterCache;

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public AzureQueueAdapterFactory(
            string name,
            AzureQueueOptions options,
            SimpleQueueCacheOptions cacheOptions,
            IQueueDataAdapter<string, IBatchContainer> dataAdapter,
            ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter)); ;
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.streamQueueMapper = new(options.QueueNames, providerName);
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
            var adapter = new AzureQueueAdapter(
                this.dataAdapter,
                this.streamQueueMapper,
                this.loggerFactory,
                this.options,
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

        public static AzureQueueAdapterFactory Create(IServiceProvider services, string name)
        {
            var azureQueueOptions = services.GetOptionsByName<AzureQueueOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var dataAdapter = services.GetServiceByName<IQueueDataAdapter<string, IBatchContainer>>(name)
                ?? services.GetService<IQueueDataAdapter<string, IBatchContainer>>();
            var factory = ActivatorUtilities.CreateInstance<AzureQueueAdapterFactory>(services, name, azureQueueOptions, cacheOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
