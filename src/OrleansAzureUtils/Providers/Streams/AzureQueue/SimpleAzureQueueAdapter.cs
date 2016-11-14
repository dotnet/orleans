using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class SimpleAzureQueueAdapter : IQueueAdapter
    {
        protected readonly string DataConnectionString;
        protected readonly string QueueName;
        protected AzureQueueDataManager Queue;

        public string Name { get ; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.WriteOnly; } }

        public SimpleAzureQueueAdapter(string dataConnectionString, string providerName, string queueName)
        {
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(queueName)) throw new ArgumentNullException("queueName");

            DataConnectionString = dataConnectionString;
            Name = providerName;
            QueueName = queueName;
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
            //int count = events.Count();
            //if (count != 1)
            //{
            //    throw new OrleansException("Trying to QueueMessageBatchAsync a batch of more than one event. " +
            //                               "SimpleAzureQueueAdapter does not support batching. Instead, you can batch in your application code.");
            //}

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
                var tmpQueue = new AzureQueueDataManager(QueueName, DataConnectionString);
                await tmpQueue.InitQueueAsync();
                if (Queue == null)
                {
                    Queue = tmpQueue;
                }
            }
            CloudQueueMessage cloudMsg = null;
            if (isBytes)
            {
                //new CloudQueueMessage(byte[]) not supported in netstandard
                cloudMsg = new CloudQueueMessage(null as string);
                cloudMsg.SetMessageContent(data as byte[]);
            }
            else if (isString)
            {
                cloudMsg = new CloudQueueMessage(data as string);
            }else if (data == null)
            {
                // It's OK to pass null data. why should I care?
                cloudMsg = new CloudQueueMessage(null as string);
            }
            await Queue.AddQueueMessage(cloudMsg);
        }
    }
}
