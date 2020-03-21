using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary> Factory class for Simple Azure Queue based stream provider.</summary>
    public class SimpleAzureQueueAdapterFactory : IQueueAdapterFactory
    {
        private readonly SimpleAzureQueueStreamOptions options;
        private string providerName;
        private ILoggerFactory loggerFactory;

        public SimpleAzureQueueAdapterFactory(string name, SimpleAzureQueueStreamOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        /// <summary>Creates the Simple Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SimpleAzureQueueAdapter(this.loggerFactory, this.options.ConnectionString, this.providerName, this.options.QueueName);
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
            return Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
        }
    }
}
