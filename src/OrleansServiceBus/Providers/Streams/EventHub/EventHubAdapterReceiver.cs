
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    internal class EventHubPartitionConfig
    {
        public IEventHubSettings Hub { get; set; }
        public ICheckpointSettings CheckpointSettings { get; set; }
        public string StreamProviderName { get; set; }
        public string Partition { get; set; }
    }

    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionConfig config;
        private readonly IObjectPool<FixedSizeBuffer> bufferPool;

        private PooledQueueCache<EventData, CachedEventHubMessage> cache;
        private EventHubReceiver receiver;
        private EventHubPartitionCheckpoint checkpoint;

        public int MaxAddCount { get { return 1000; } }

        public EventHubAdapterReceiver(EventHubPartitionConfig partitionConfig, IObjectPool<FixedSizeBuffer> bufferPool, Logger log)
        {
            config = partitionConfig;
            this.bufferPool = bufferPool;
        }

        public async Task Initialize(TimeSpan timeout)
        {
            var dataAdapter = new EventHubDataAdapter(bufferPool);
            cache = new PooledQueueCache<EventData, CachedEventHubMessage>(dataAdapter) { OnPurged = OnPurged };
            dataAdapter.PurgeAction = cache.Purge;
            checkpoint = await EventHubPartitionCheckpoint.Create(config.CheckpointSettings, config.StreamProviderName, config.Partition);
            string offset = await checkpoint.Load();
            receiver = await CreateReceiver(config, offset);
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            var localReciever = receiver;
            if (localReciever == null)
            {
                return new List<IBatchContainer>();
            }
            List<EventData> messages = (await localReciever.ReceiveAsync(maxCount, ReceiveTimeout)).ToList();

            var batches = new List<IBatchContainer>();
            if (messages.Count == 0)
            {
                return batches;
            }
            foreach (EventData message in messages)
            {
                cache.Add(message);
                batches.Add(new StreamActivityNotificationBatch(Guid.Parse(message.PartitionKey),
                    message.GetStreamNamespaceProperty(), new EventSequenceToken(message.SequenceNumber, 0)));

            }

            if (!checkpoint.Exists)
            {
                checkpoint.Update(messages[0].Offset, DateTime.UtcNow);
            }
            return batches;
        }

        public void AddToCache(IList<IBatchContainer> messages)
        {
            // do nothing, we add data directly into cache.  No need for agent involvement
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

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return TaskDone.Done;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            if (cache != null)
            {
                cache.OnPurged = null;
            }
            EventHubReceiver localReceiver = Interlocked.Exchange(ref receiver, null);
            if (localReceiver != null)
            {
                await localReceiver.CloseAsync();
            }
        }

        private static Task<EventHubReceiver> CreateReceiver(EventHubPartitionConfig partitionConfig, string offset)
        {
            EventHubClient client = EventHubClient.CreateFromConnectionString(partitionConfig.Hub.ConnectionString, partitionConfig.Hub.Path);
            EventHubConsumerGroup consumerGroup = client.GetConsumerGroup(partitionConfig.Hub.ConsumerGroup);
            if (partitionConfig.Hub.PrefetchCount.HasValue)
            {
                consumerGroup.PrefetchCount = partitionConfig.Hub.PrefetchCount.Value;
            }
            // if we have a starting offset or if we're not configured to start reading from utc now, read from offset
            if (!partitionConfig.Hub.StartFromNow || offset != EventHubConsumerGroup.StartOfStream)
            {
                return consumerGroup.CreateReceiverAsync(partitionConfig.Partition, offset, true);
            }
            return consumerGroup.CreateReceiverAsync(partitionConfig.Partition, DateTime.UtcNow);
        }

        private void OnPurged(CachedEventHubMessage lastItemPurged)
        {
            int readOffset = 0;
            SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read namespace, not needed so throw away.
            string offset = SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read offset
            checkpoint.Update(offset, DateTime.UtcNow);
        }

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

            public void RecordDeliveryFailure()
            {
            }
        }
    }
}
