using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Storage;

namespace OrleansAWSUtils.Streams
{
    internal class SQSAdapter : IQueueAdapter
    {
        protected readonly string ServiceId;
        private readonly ISQSDataAdapter dataAdapter;
        private readonly SqsOptions sqsOptions;
        private readonly IConsistentRingStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, SQSStorage> Queues = new ConcurrentDictionary<QueueId, SQSStorage>();
        private readonly ILoggerFactory loggerFactory;
        public string Name { get; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public SQSAdapter(ISQSDataAdapter dataAdapter, IConsistentRingStreamQueueMapper streamQueueMapper, ILoggerFactory loggerFactory, SqsOptions sqsOptions, string serviceId, string providerName)
        {
            ArgumentNullException.ThrowIfNull(dataAdapter);
            ArgumentNullException.ThrowIfNull(streamQueueMapper);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(sqsOptions);
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));
            this.loggerFactory = loggerFactory;
            this.sqsOptions = sqsOptions;
            this.dataAdapter = dataAdapter;
            this.ServiceId = serviceId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return SQSAdapterReceiver.Create(this.dataAdapter, this.loggerFactory, queueId, sqsOptions, this.ServiceId);
        }

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        {
            if (token != null)
            {
                throw new ArgumentException("SQSStream stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            }
            var queueId = streamQueueMapper.GetQueueForStream(streamId);
            if (!Queues.TryGetValue(queueId, out var queue))
            {
                var tmpQueue = new SQSStorage(this.loggerFactory, queueId.ToString(), sqsOptions, this.ServiceId);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }

            var sqsMessage = dataAdapter.ToQueueMessage(streamId, events, token, requestContext);
            var sqsRequest = new SendMessageRequest(string.Empty, sqsMessage.Body);
            if (this.sqsOptions.FifoQueue)
            {
                sqsRequest.MessageGroupId = CreateMessageGroupId(streamId);
                sqsRequest.MessageDeduplicationId = Guid.NewGuid().ToString("N");
            }

            if (sqsMessage.MessageAttributes is { Count: > 0 } messageAttributes)
            {
                sqsRequest.MessageAttributes = new Dictionary<string, MessageAttributeValue>(messageAttributes);
            }
            await queue.AddMessage(sqsRequest);
        }

        private static string CreateMessageGroupId(StreamId streamId)
        {
            return Convert.ToHexString(SHA256.HashData(streamId.FullKey.Span));
        }
    }
}
