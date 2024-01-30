using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Orleans.Configuration;
using Orleans;
using Orleans.Configuration.Overrides;
using Orleans.Streaming.SQS.Streams;
using Orleans.Runtime;

namespace OrleansAWSUtils.Streams
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class SQSAdapterFactory : IQueueAdapterFactory
    {
        private readonly string providerName;
        private readonly SqsOptions sqsOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ISQSDataAdapter dataAdapter;
        private readonly ILoggerFactory loggerFactory;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        private readonly IQueueAdapterCache adapterCache;

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public SQSAdapterFactory(
            string name, 
            SqsOptions sqsOptions,
            HashRingStreamQueueMapperOptions queueMapperOptions,
            SimpleQueueCacheOptions cacheOptions,
            IOptions<ClusterOptions> clusterOptions, 
            ISQSDataAdapter dataAdapter, 
            ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.sqsOptions = sqsOptions;
            this.clusterOptions = clusterOptions.Value;
            this.dataAdapter = dataAdapter;
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

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SQSAdapter(this.dataAdapter, this.streamQueueMapper, this.loggerFactory, this.sqsOptions, this.clusterOptions.ServiceId, this.providerName);
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

        public static SQSAdapterFactory Create(IServiceProvider services, string name)
        {
            var sqsOptions = services.GetOptionsByName<SqsOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);
            var dataAdapter = services.GetKeyedService<ISQSDataAdapter>(name)
                               ?? services.GetService<ISQSDataAdapter>()
                               ?? ActivatorUtilities.CreateInstance<SQSDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<SQSAdapterFactory>(services, name, sqsOptions, cacheOptions, queueMapperOptions, clusterOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
