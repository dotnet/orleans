
using System;
using System.Threading.Tasks;
using Orleans.Streams;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class AzureQueueAdapterFactory : IQueueAdapterFactory
    {
        internal const int CacheSizeDefaultValue = 4096;
        internal const string NumQueuesPropertyName = "NumQueues";

        /// <summary> Default number of Azure Queue used in this stream provider.</summary>
        public const int NumQueuesDefaultValue = 8; // keep as power of 2.
        
        private string deploymentId;
        private string dataConnectionString;
        private string providerName;
        private int cacheSize;
        private int numQueues;
        private HashRingBasedStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;

        /// <summary>"DataConnectionString".</summary>
        public const string DataConnectionStringPropertyName = "DataConnectionString";
        /// <summary>"DeploymentId".</summary>
        public const string DeploymentIdPropertyName = "DeploymentId";

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        /// <summary> Init the factory.</summary>
        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger, IServiceProvider serviceProvider)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!config.Properties.TryGetValue(DataConnectionStringPropertyName, out dataConnectionString))
                throw new ArgumentException(String.Format("{0} property not set", DataConnectionStringPropertyName));
            if (!config.Properties.TryGetValue(DeploymentIdPropertyName, out deploymentId))
                throw new ArgumentException(String.Format("{0} property not set", DeploymentIdPropertyName));

            cacheSize = SimpleQueueAdapterCache.ParseSize(config, CacheSizeDefaultValue);

            string numQueuesString;
            numQueues = NumQueuesDefaultValue;
            if (config.Properties.TryGetValue(NumQueuesPropertyName, out numQueuesString))
            {
                if (!int.TryParse(numQueuesString, out numQueues))
                    throw new ArgumentException(String.Format("{0} invalid.  Must be int", NumQueuesPropertyName));
            }

            this.providerName = providerName;
            streamQueueMapper = new HashRingBasedStreamQueueMapper(numQueues, providerName);
            adapterCache = new SimpleQueueAdapterCache(cacheSize, logger);
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));
            }
        }

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new AzureQueueAdapter(streamQueueMapper, dataConnectionString, deploymentId, providerName);
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
    }
}
