
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class AzureQueueAdapter<TDataAdapter> : IQueueAdapter
        where TDataAdapter : IAzureQueueDataAdapter, new()
    {
        protected readonly string DeploymentId;
        protected readonly string DataConnectionString;
        protected readonly TimeSpan? MessageVisibilityTimeout;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();
        protected readonly IAzureQueueDataAdapter dataAdapter;

        public string Name { get ; }
        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public AzureQueueAdapter(HashRingBasedStreamQueueMapper streamQueueMapper, string dataConnectionString, string deploymentId, string providerName, TimeSpan? messageVisibilityTimeout = null)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException(nameof(dataConnectionString));
            if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException(nameof(deploymentId));
            
            DataConnectionString = dataConnectionString;
            DeploymentId = deploymentId;
            Name = providerName;
            MessageVisibilityTimeout = messageVisibilityTimeout;
            this.streamQueueMapper = streamQueueMapper;
            this.dataAdapter = new TDataAdapter();
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(queueId, DataConnectionString, DeploymentId, this.dataAdapter, MessageVisibilityTimeout);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null) throw new ArgumentException("AzureQueue stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(queueId.ToString(), DeploymentId, DataConnectionString, MessageVisibilityTimeout);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = this.dataAdapter.ToCloudQueueMessage(streamGuid, streamNamespace, events, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
