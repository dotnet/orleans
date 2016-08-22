using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OrleansAWSUtils.Streams
{
    internal class SQSAdapter : IQueueAdapter
    {
        protected readonly string DeploymentId;
        protected readonly string DataConnectionString;
        private readonly IConsistentRingStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, SQSStorage> Queues = new ConcurrentDictionary<QueueId, SQSStorage>();

        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public SQSAdapter(IConsistentRingStreamQueueMapper streamQueueMapper, string dataConnectionString, string deploymentId, string providerName)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");

            DataConnectionString = dataConnectionString;
            DeploymentId = deploymentId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return SQSAdapterReceiver.Create(queueId, DataConnectionString, DeploymentId);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new ArgumentException("SQSStream stream provider currebtly does not support non-null StreamSequenceToken.", "token");
            }
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
            SQSStorage queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new SQSStorage(queueId.ToString(), DataConnectionString, DeploymentId);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var msg = SQSBatchContainer.ToSQSMessage(streamGuid, streamNamespace, events, requestContext);
            await queue.AddMessage(msg);
        }
    }
}
