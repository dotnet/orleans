using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Storage.Queue;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class AzureQueueAdapter : IQueueAdapter
    {
        protected readonly string ServiceId;
        private readonly SerializationManager serializationManager;
        protected readonly AzureQueueOptions queueOptions;
        private readonly IAzureStreamQueueMapper streamQueueMapper;
        private readonly ILoggerFactory loggerFactory;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();
        protected readonly IQueueDataAdapter<CloudQueueMessage, IBatchContainer> dataAdapter;

        public string Name { get; }
        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public AzureQueueAdapter(
            IQueueDataAdapter<CloudQueueMessage, IBatchContainer> dataAdapter,
            SerializationManager serializationManager,
            IAzureStreamQueueMapper streamQueueMapper,
            ILoggerFactory loggerFactory,
            AzureQueueOptions queueOptions,
            string serviceId,
            string providerName)
        {
            this.serializationManager = serializationManager;
            this.queueOptions = queueOptions;
            ServiceId = serviceId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
            this.dataAdapter = dataAdapter;
            this.loggerFactory = loggerFactory;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(this.serializationManager, this.loggerFactory, this.streamQueueMapper.PartitionToAzureQueue(queueId),
                queueOptions.ConnectionString, this.dataAdapter, queueOptions.MessageVisibilityTimeout);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null) throw new ArgumentException("AzureQueue stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(this.loggerFactory, this.streamQueueMapper.PartitionToAzureQueue(queueId), queueOptions.ConnectionString, queueOptions.MessageVisibilityTimeout);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = this.dataAdapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
