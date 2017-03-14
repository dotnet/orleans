using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Simple Azure Queue based stream provider.</summary>
    public class SimpleAzureQueueAdapterFactory : IQueueAdapterFactory
    {
        private string dataConnectionString;
        private string queueName;
        private string providerName;

        /// <summary>"QueueName".</summary>
        public const string QUEUE_NAME_STRING = "QueueName";

        /// <summary> Init the factory.</summary>
        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger, IServiceProvider serviceProvider)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!config.Properties.TryGetValue(AzureQueueAdapterConstants.DataConnectionStringPropertyName, out dataConnectionString))
                throw new ArgumentException(String.Format("{0} property not set", AzureQueueAdapterConstants.DataConnectionStringPropertyName));
            if (!config.Properties.TryGetValue(QUEUE_NAME_STRING, out queueName))
                throw new ArgumentException(String.Format("{0} property not set", QUEUE_NAME_STRING));

            this.providerName = providerName;
        }


        /// <summary>Creates the Simple Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SimpleAzureQueueAdapter(dataConnectionString, providerName, queueName);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        /// <summary>Creates the adapter cache.</summary>
        public virtual IQueueAdapterCache GetQueueAdapterCache()
        {
            throw new OrleansException("SimpleAzureQueueAdapter is a write-only adapter, it does not support reading from the queue and thus does not need cache.");
        }

        /// <summary>Creates the factory stream queue mapper.</summary>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            throw new OrleansException("SimpleAzureQueueAdapter does not support multiple queues, it only writes to one queue.");
        }

        /// <summary>
        /// Creates a delivery failure handler for the specified queue.
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler(false));
        }
    }
}
