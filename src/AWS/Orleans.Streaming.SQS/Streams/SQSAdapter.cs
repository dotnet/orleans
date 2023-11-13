using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streaming.SQS.Streams;

namespace OrleansAWSUtils.Streams
{
    internal class SQSAdapter : IQueueAdapter
    {
        protected readonly string ServiceId;
        private readonly ISQSDataAdapter dataAdapter;
        protected readonly string DataConnectionString;
        private readonly IConsistentRingStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, SQSStorage> Queues = new ConcurrentDictionary<QueueId, SQSStorage>();
        private readonly ILoggerFactory loggerFactory;
        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public SQSAdapter(ISQSDataAdapter dataAdapter, IConsistentRingStreamQueueMapper streamQueueMapper, ILoggerFactory loggerFactory, string dataConnectionString, string serviceId, string providerName)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException(nameof(dataConnectionString));
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));
            this.loggerFactory = loggerFactory;
            this.dataAdapter = dataAdapter;
            DataConnectionString = dataConnectionString;
            this.ServiceId = serviceId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return SQSAdapterReceiver.Create(this.dataAdapter, this.loggerFactory, queueId, DataConnectionString, this.ServiceId);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new ArgumentException("SQSStream stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            }
            var queueId = streamQueueMapper.GetQueueForStream(streamId);
            SQSStorage queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new SQSStorage(this.loggerFactory, queueId.ToString(), DataConnectionString, this.ServiceId);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }

            var sqsMessage = dataAdapter.ToQueueMessage(streamId, events, token, requestContext);
            var sqsRequest = new SendMessageRequest(string.Empty, sqsMessage.Body);
            foreach (var attr in sqsMessage.Attributes)
            {
                sqsRequest.MessageAttributes.Add(attr.Key, new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = attr.Value
                });
            }
            await queue.AddMessage(sqsRequest);
        }
    }
}
