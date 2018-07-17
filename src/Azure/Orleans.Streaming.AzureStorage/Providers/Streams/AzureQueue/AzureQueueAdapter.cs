using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue;
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
        private readonly IAzureStreamQueueMapper streamQueueMapper;
        private readonly ILoggerFactory loggerFactory;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();
        protected readonly IAzureQueueDataAdapter dataAdapter;

        public string Name { get ; }
        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public AzureQueueAdapter(
            TDataAdapter dataAdapter,
            SerializationManager serializationManager,
            IAzureStreamQueueMapper streamQueueMapper,
            ILoggerFactory loggerFactory,
            AzureQueueOptions queueOptions,
            string serviceId,
            string providerName)
        {
            this.serializationManager = serializationManager;
            DataConnectionString = queueOptions.ConnectionString;
            ServiceId = serviceId;
            Name = providerName;
            MessageVisibilityTimeout = queueOptions.MessageVisibilityTimeout;
            this.streamQueueMapper = streamQueueMapper;
            this.dataAdapter = dataAdapter;
            this.loggerFactory = loggerFactory;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(this.serializationManager, this.loggerFactory, this.streamQueueMapper.PartitionToAzureQueue(queueId), DataConnectionString, this.dataAdapter, MessageVisibilityTimeout);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null) throw new ArgumentException("AzureQueue stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(this.loggerFactory, this.streamQueueMapper.PartitionToAzureQueue(queueId), DataConnectionString, MessageVisibilityTimeout);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = this.dataAdapter.ToCloudQueueMessage(streamGuid, streamNamespace, events, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
