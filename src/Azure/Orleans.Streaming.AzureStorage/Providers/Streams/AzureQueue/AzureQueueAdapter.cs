using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal sealed class AzureQueueAdapter : IQueueAdapter
    {
        private readonly AzureQueueOptions queueOptions;
        private readonly HashRingBasedPartitionedStreamQueueMapper streamQueueMapper;
        private readonly ILoggerFactory loggerFactory;
        private readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new();
        private readonly IQueueDataAdapter<string, IBatchContainer> dataAdapter;

        public string Name { get; }
        public bool IsRewindable => false;

        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public AzureQueueAdapter(
            IQueueDataAdapter<string, IBatchContainer> dataAdapter,
            HashRingBasedPartitionedStreamQueueMapper streamQueueMapper,
            ILoggerFactory loggerFactory,
            AzureQueueOptions queueOptions,
            string providerName)
        {
            this.queueOptions = queueOptions;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
            this.dataAdapter = dataAdapter;
            this.loggerFactory = loggerFactory;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId) => AzureQueueAdapterReceiver.Create(loggerFactory, streamQueueMapper.QueueToPartition(queueId), queueOptions, dataAdapter);

        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if(token != null) throw new ArgumentException("AzureQueue stream provider currently does not support non-null StreamSequenceToken.", nameof(token));
            var queueId = streamQueueMapper.GetQueueForStream(streamId);
            if (!Queues.TryGetValue(queueId, out var queue))
            {
                var tmpQueue = new AzureQueueDataManager(loggerFactory, streamQueueMapper.QueueToPartition(queueId), queueOptions);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = this.dataAdapter.ToQueueMessage(streamId, events, null, requestContext);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
