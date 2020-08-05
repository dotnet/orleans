using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class SimpleAzureQueueAdapter : IQueueAdapter
    {
        private readonly SimpleAzureQueueStreamOptions options;
        protected AzureQueueDataManager Queue;
        private readonly ILoggerFactory loggerFactory;
        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.WriteOnly; } }

        public SimpleAzureQueueAdapter(ILoggerFactory loggerFactory, SimpleAzureQueueStreamOptions options, string providerName)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.loggerFactory = loggerFactory;
            Name = providerName;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            throw new OrleansException("SimpleAzureQueueAdapter is a write-only adapter, it does not support reading from the queue.");
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (events == null)
            {
                throw new ArgumentNullException("events", "Trying to QueueMessageBatchAsync null data.");
            }

            object data = events.First();
            bool isBytes = data is byte[];
            bool isString = data is string;
            if (data != null && !isBytes && !isString)
            {
                throw new OrleansException(
                    string.Format(
                        "Trying to QueueMessageBatchAsync a type {0} which is not a byte[] and not string. " +
                        "SimpleAzureQueueAdapter only supports byte[] or string.", data.GetType()));
            }

            if (Queue == null)
            {
                var tmpQueue = new AzureQueueDataManager(this.loggerFactory, options.QueueName,
                    new AzureQueueOptions { ConnectionString = options.ConnectionString, ServiceUri = options.ServiceUri, TokenCredential = options.TokenCredential });
                await tmpQueue.InitQueueAsync();
                if (Queue == null)
                {
                    Queue = tmpQueue;
                }
            }

            string cloudMsg = null;
            if (isBytes)
            {
                cloudMsg = Convert.ToBase64String(data as byte[]);
            }
            else if (isString)
            {
                cloudMsg = data as string;
            }

            await Queue.AddQueueMessage(cloudMsg);
        }
    }
}
