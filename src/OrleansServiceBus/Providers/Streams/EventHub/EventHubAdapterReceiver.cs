
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansServiceBusUtils.Providers.Streams.EventHub;

namespace Orleans.ServiceBus.Providers.Streams.EventHub
{
    internal class EventHubPartitionConfig
    {
        public IEventHubSettings Hub { get; set; }
        public string Partition { get; set; }
        public int CacheSize { get; set; }
    }

    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionConfig config;
        private readonly Logger logger;

        private IQueueCache cache;
        private EventHubReceiver receiver;

        public int MaxAddCount { get { return cache.MaxAddCount; } }

        public EventHubAdapterReceiver(EventHubPartitionConfig partitionConfig, Logger log)
        {
            config = partitionConfig;
            logger = log;
        }

        public void AddToCache(IList<IBatchContainer> messages)
        {
            cache.AddToCache(messages);
        }

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            return cache.TryPurgeFromCache(out purgedItems);
        }

        public IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            return cache.GetCacheCursor(streamGuid, streamNamespace, token);
        }

        public bool IsUnderPressure()
        {
            return cache.IsUnderPressure();
        }

        private static Task<EventHubReceiver> CreateReceiver(EventHubPartitionConfig partitionConfig)
        {
            EventHubClient client = EventHubClient.CreateFromConnectionString(partitionConfig.Hub.ConnectionString, partitionConfig.Hub.Path);
            EventHubConsumerGroup consumerGroup = client.GetConsumerGroup(partitionConfig.Hub.ConsumerGroup);
            if (partitionConfig.Hub.PrefetchCount.HasValue)
            {
                consumerGroup.PrefetchCount = partitionConfig.Hub.PrefetchCount.Value;
            }
            return consumerGroup.CreateReceiverAsync(partitionConfig.Partition, DateTime.UtcNow);
        }

        public async Task Initialize(TimeSpan timeout)
        {
            cache = new SimpleQueueCache(config.CacheSize, logger);
            receiver = await CreateReceiver(config);
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            IEnumerable<EventData> messages = await receiver.ReceiveAsync(maxCount, ReceiveTimeout);
            return BuildBatchList(messages);
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return TaskDone.Done;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            EventHubReceiver localReceiver = Interlocked.Exchange(ref receiver, null);
            if (localReceiver != null)
            {
                await localReceiver.CloseAsync();
            }
        }

        private IList<IBatchContainer> BuildBatchList(IEnumerable<EventData> messages)
        {
            return messages.Select(EventHubBatchContainer.FromEventData).ToList();
        }
    }
}
