
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
        public string Partition { get; set; }
    }


    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public const int MaxMessagesPerRead = 1000;
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionConfig config;
        private readonly Func<string, IStreamQueueCheckpointer<string>, IEventHubQueueCache> cacheFactory;
        private readonly Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory;

        private IEventHubQueueCache cache;
        private EventHubReceiver receiver;
        private IStreamQueueCheckpointer<string> checkpointer;
        private AggregatedQueueFlowController flowController;

        public int GetMaxAddCount() { return flowController.GetMaxAddCount(); }

        public EventHubAdapterReceiver(EventHubPartitionConfig partitionConfig,
            Func<string, IStreamQueueCheckpointer<string>,IEventHubQueueCache> cacheFactory,
            Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            Logger log)
        {
            this.cacheFactory = cacheFactory;
            this.checkpointerFactory = checkpointerFactory;
            config = partitionConfig;
        }

        public async Task Initialize(TimeSpan timeout)
        {
            checkpointer = await checkpointerFactory(config.Partition);
            cache = cacheFactory(config.Partition, checkpointer);
            flowController = new AggregatedQueueFlowController(MaxMessagesPerRead) { cache };
            string offset = await checkpointer.Load();
            receiver = await CreateReceiver(config, offset);
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            if (receiver == null || maxCount <= 0)
            {
                return new List<IBatchContainer>();
            }
            List<EventData> messages = (await receiver.ReceiveAsync(maxCount, ReceiveTimeout)).ToList();

            var batches = new List<IBatchContainer>();
            if (messages.Count == 0)
            {
                return batches;
            }
            foreach (EventData message in messages)
            {
                StreamPosition streamPosition = cache.Add(message);
                batches.Add(new StreamActivityNotificationBatch(streamPosition.StreamIdentity.Guid,
                    streamPosition.StreamIdentity.Namespace, streamPosition.SequenceToken));
            }

            if (!checkpointer.CheckpointExists)
            {
                checkpointer.Update(messages[0].Offset, DateTime.UtcNow);
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

        public IQueueCacheCursor GetCacheCursor(IStreamIdentity streamIdentity, StreamSequenceToken token)
        {
            return new Cursor(cache, streamIdentity, token);
        }

        public bool IsUnderPressure()
        {
            return false;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return TaskDone.Done;
        }

        public Task Shutdown(TimeSpan timeout)
        {
            // clear cache and receiver
            IEventHubQueueCache localCache = Interlocked.Exchange(ref cache, null);
            EventHubReceiver localReceiver = Interlocked.Exchange(ref receiver, null);
            // start closing receiver
            Task closeTask = TaskDone.Done;
            if (localReceiver != null)
            {
                closeTask = localReceiver.CloseAsync();
            }
            // dispose of cache
            if (localCache != null)
            {
                localCache.Dispose();
            }
            // finish return receiver closing task
            return closeTask;
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
            private readonly IEventHubQueueCache cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(IEventHubQueueCache cache, IStreamIdentity streamIdentity, StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamIdentity, token);
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
