
using System;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class AzureQueueAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IAzureQueueDataAdapter, new()
    {
        private string deploymentId;
        private string dataConnectionString;
        private string providerName;
        private int cacheSize;
        private int numQueues;
        private TimeSpan? messageVisibilityTimeout;
        private HashRingBasedStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        /// <summary> Init the factory.</summary>
        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger, IServiceProvider serviceProvider)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!config.Properties.TryGetValue(AzureQueueAdapterConstants.DataConnectionStringPropertyName, out dataConnectionString))
                throw new ArgumentException($"{AzureQueueAdapterConstants.DataConnectionStringPropertyName} property not set");
            if (!config.Properties.TryGetValue(AzureQueueAdapterConstants.DeploymentIdPropertyName, out deploymentId))
                throw new ArgumentException($"{AzureQueueAdapterConstants.DeploymentIdPropertyName} property not set");
            string messageVisibilityTimeoutRaw;
            if (config.Properties.TryGetValue(AzureQueueAdapterConstants.MessageVisibilityTimeoutPropertyName, out messageVisibilityTimeoutRaw))
            {
                TimeSpan messageVisibilityTimeoutTemp;
                if (!TimeSpan.TryParse(messageVisibilityTimeoutRaw, out messageVisibilityTimeoutTemp))
                {
                    throw new ArgumentException(
                        $"Failed to parse {AzureQueueAdapterConstants.MessageVisibilityTimeoutPropertyName} value '{messageVisibilityTimeoutRaw}' as a TimeSpan");
                }

                messageVisibilityTimeout = messageVisibilityTimeoutTemp;
            }
            else
            {
                messageVisibilityTimeout = null;
            }
            
            cacheSize = SimpleQueueAdapterCache.ParseSize(config, AzureQueueAdapterConstants.CacheSizeDefaultValue);

            string numQueuesString;
            numQueues = AzureQueueAdapterConstants.NumQueuesDefaultValue;
            if (config.Properties.TryGetValue(AzureQueueAdapterConstants.NumQueuesPropertyName, out numQueuesString))
            {
                if (!int.TryParse(numQueuesString, out numQueues))
                    throw new ArgumentException($"{AzureQueueAdapterConstants.NumQueuesPropertyName} invalid.  Must be int");
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
            var adapter = new AzureQueueAdapter<TDataAdapter>(streamQueueMapper, dataConnectionString, deploymentId, providerName, messageVisibilityTimeout);
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
