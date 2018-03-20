using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class AzureQueueAdapter<TDataAdapter> : IQueueAdapter
        where TDataAdapter : IAzureQueueDataAdapter
    {
        protected readonly string ServiceId;
        private readonly SerializationManager serializationManager;
        protected readonly string DataConnectionString;
        protected readonly TimeSpan? MessageVisibilityTimeout;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        private readonly ILoggerFactory loggerFactory;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();
        protected readonly IAzureQueueDataAdapter dataAdapter;

        public string Name { get ; }
        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public AzureQueueAdapter(
            TDataAdapter dataAdapter,
            SerializationManager serializationManager,
            HashRingBasedStreamQueueMapper streamQueueMapper,
            ILoggerFactory loggerFactory,
            string dataConnectionString,
            string serviceId,
            string providerName,
            TimeSpan? messageVisibilityTimeout = null)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException(nameof(dataConnectionString));
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));

            this.serializationManager = serializationManager;
            DataConnectionString = dataConnectionString;
            ServiceId = serviceId;
            Name = providerName;
            MessageVisibilityTimeout = messageVisibilityTimeout;
            this.streamQueueMapper = streamQueueMapper;
            this.dataAdapter = dataAdapter;
            this.loggerFactory = loggerFactory;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(this.serializationManager, this.loggerFactory, queueId, DataConnectionString, ServiceId, this.dataAdapter, MessageVisibilityTimeout);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null) throw new ArgumentException("AzureQueue stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(this.loggerFactory, queueId.ToString(), ServiceId, DataConnectionString, MessageVisibilityTimeout);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = this.dataAdapter.ToCloudQueueMessage(streamGuid, streamNamespace, events, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
