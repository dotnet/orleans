
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
    }

    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly ICacheDataAdapter<EventData, CachedEventHubMessage> dataAdapter;
        private readonly EventHubPartitionConfig config;

        private PooledQueueCache<EventData, CachedEventHubMessage> cache;
        private EventHubReceiver receiver;

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache<EventData, CachedEventHubMessage> cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache<EventData, CachedEventHubMessage> cache, Guid streamGuid, string streamNamespace, StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamGuid, streamNamespace, token);
            }

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null;
                return current;
            }

            public bool MoveNext()
            {
                IBatchContainer next;
                if (!cache.TryGetNextMessage(cursor, out next))
                {
                    return false;
                }

                current = next;
                return true;
            }

            public void Refresh()
            {
            }
        }

        public int MaxAddCount { get { return 1000; } }

        public EventHubAdapterReceiver(EventHubPartitionConfig partitionConfig, IObjectPool<FixedSizeBuffer> bufferPool, Logger log)
        {
            dataAdapter = new EventHubDataAdapter(bufferPool, Purge);
            config = partitionConfig;
        }

        public void AddToCache(IList<IBatchContainer> messages)
        {
            // do nothing, we add data directly into cache.  No need for agent involvment
        }

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            return false;
        }

        public IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            return new Cursor(cache, streamGuid, streamNamespace, token);
        }

        public bool IsUnderPressure()
        {
            return false;
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
            cache = new PooledQueueCache<EventData, CachedEventHubMessage>(dataAdapter);
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
            var batches = new List<IBatchContainer>();
            foreach (EventData message in messages)
            {
                cache.Add(message);
                batches.Add(new StreamActivityNotificationBatch(Guid.Parse(message.PartitionKey),
                    message.GetStreamNamespaceProperty(), new EventSequenceToken(message.SequenceNumber, 0)));
                
            }
            return batches;
        }

        private void Purge(IDisposable purgedResource)
        {
            cache.Purge(purgedResource);
        }

        /// <summary>
        /// This batch is primarily used to notify the adapter of stream activity.  It is never delivered
        ///   to consumers, so does not need to be serializable.
        /// </summary>
        private class StreamActivityNotificationBatch : IBatchContainer
        {
            public Guid StreamGuid { get; private set; }
            public string StreamNamespace { get; private set; }
            public StreamSequenceToken SequenceToken { get; private set; }

            public StreamActivityNotificationBatch(Guid streamGuid, string streamNamespace,
                StreamSequenceToken sequenceToken)
            {
                StreamGuid = streamGuid;
                StreamNamespace = streamNamespace;
                SequenceToken = sequenceToken;
            }

            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() { throw new NotSupportedException(); }
            public bool ImportRequestContext() { throw new NotSupportedException(); }
            public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc) { throw new NotSupportedException(); }
        }
    }
}
