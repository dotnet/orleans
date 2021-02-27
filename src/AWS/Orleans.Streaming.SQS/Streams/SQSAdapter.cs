using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;

namespace OrleansAWSUtils.Streams
{
    internal class SQSAdapter : IQueueAdapter
    {
        protected readonly string ServiceId;
        private readonly Serializer<SQSBatchContainer> serializer;
        protected readonly string DataConnectionString;
        private readonly IConsistentRingStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, SQSStorage> Queues = new ConcurrentDictionary<QueueId, SQSStorage>();
        private readonly ILoggerFactory loggerFactory;
        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public SQSAdapter(Serializer<SQSBatchContainer> serializer, IConsistentRingStreamQueueMapper streamQueueMapper, ILoggerFactory loggerFactory, string dataConnectionString, string serviceId, string providerName)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));
            this.loggerFactory = loggerFactory;
            this.serializer = serializer;
            DataConnectionString = dataConnectionString;
            this.ServiceId = serviceId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return SQSAdapterReceiver.Create(this.serializer, this.loggerFactory, queueId, DataConnectionString, this.ServiceId);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new ArgumentException("SQSStream stream provider currently does not support non-null StreamSequenceToken.", "token");
            }
            var queueId = streamQueueMapper.GetQueueForStream(streamId);
            SQSStorage queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new SQSStorage(this.loggerFactory, queueId.ToString(), DataConnectionString, this.ServiceId);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var msg = SQSBatchContainer.ToSQSMessage(this.serializer, streamId, events, requestContext);
            await queue.AddMessage(msg);
        }
    }
}
