using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class AzureQueueAdapter : IQueueAdapter
    {
        protected readonly string DeploymentId;
        protected readonly string DataConnectionString;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();

        public string Name { get ; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public AzureQueueAdapter(HashRingBasedStreamQueueMapper streamQueueMapper, string dataConnectionString, string deploymentId, string providerName)
        {
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");
            
            DataConnectionString = dataConnectionString;
            DeploymentId = deploymentId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(queueId, DataConnectionString, DeploymentId);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null)
            {
                throw new ArgumentException("AzureQueue stream provider currebtly does not support non-null StreamSequenceToken.", "token");
            }
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(queueId.ToString(), DeploymentId, DataConnectionString);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = AzureQueueBatchContainer.ToCloudQueueMessage(streamGuid, streamNamespace, events, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
