using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

namespace OrleansAWSUtils.Streams
{
    internal class SQSAdapter : IQueueAdapter
    {
        protected readonly string ClusterId;
        private readonly SerializationManager serializationManager;
        protected readonly string DataConnectionString;
        private readonly IConsistentRingStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, SQSStorage> Queues = new ConcurrentDictionary<QueueId, SQSStorage>();
        private readonly ILoggerFactory loggerFactory;
        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public SQSAdapter(SerializationManager serializationManager, IConsistentRingStreamQueueMapper streamQueueMapper, ILoggerFactory loggerFactory, string dataConnectionString, string clusterId, string providerName)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (string.IsNullOrEmpty(clusterId)) throw new ArgumentNullException(nameof(clusterId));
            this.loggerFactory = loggerFactory;
            this.serializationManager = serializationManager;
            DataConnectionString = dataConnectionString;
            this.ClusterId = clusterId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return SQSAdapterReceiver.Create(this.serializationManager, this.loggerFactory, queueId, DataConnectionString, this.ClusterId);
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
                var tmpQueue = new SQSStorage(this.loggerFactory, queueId.ToString(), DataConnectionString, this.ClusterId);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var msg = SQSBatchContainer.ToSQSMessage(this.serializationManager, streamGuid, streamNamespace, events, requestContext);
            await queue.AddMessage(msg);
        }
    }
}
