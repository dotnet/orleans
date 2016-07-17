
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Func<string, IStreamQueueCheckpointer<string>, Logger, IEventHubQueueCache> cacheFactory;
        private readonly Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory;
        private readonly Logger baseLogger;
        private readonly Logger logger;

        // metric names
        private readonly string hubReceiveTimeMetric;
        private readonly string partitionReceiveTimeMetric;
        private readonly string hubReadFailure;
        private readonly string partitionReadFailure;
        private readonly string hubMessagesRecieved;
        private readonly string partitionMessagesReceived;
        private readonly string hubAgeOfMessagesBeingProcessed;
        private readonly string partitionAgeOfMessagesBeingProcessed;

        private IEventHubQueueCache cache;
        private EventHubReceiver receiver;
        private IStreamQueueCheckpointer<string> checkpointer;
        private AggregatedQueueFlowController flowController;

        // Receiver life cycle
        private int recieverState = ReceiverShutdown;
        private const int ReceiverShutdown = 0;
        private const int ReceiverRunning = 1;

        public int GetMaxAddCount() { return flowController.GetMaxAddCount(); }

        public EventHubAdapterReceiver(EventHubPartitionConfig partitionConfig,
            Func<string, IStreamQueueCheckpointer<string>, Logger,IEventHubQueueCache> cacheFactory,
            Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            Logger logger)
        {
            this.cacheFactory = cacheFactory;
            this.checkpointerFactory = checkpointerFactory;
            baseLogger = logger;
            this.logger = logger.GetSubLogger("-receiver");
            config = partitionConfig;

            hubReceiveTimeMetric = $"Orleans.ServiceBus.EventHub.ReceiveTime_{config.Hub.Path}";
            partitionReceiveTimeMetric = $"Orleans.ServiceBus.EventHub.ReceiveTime_{config.Hub.Path}-{config.Partition}";
            hubReadFailure = $"Orleans.ServiceBus.EventHub.ReadFailure_{config.Hub.Path}";
            partitionReadFailure = $"Orleans.ServiceBus.EventHub.ReadFailure_{config.Hub.Path}-{config.Partition}";
            hubMessagesRecieved = $"Orleans.ServiceBus.EventHub.MessagesReceived_{config.Hub.Path}";
            partitionMessagesReceived = $"Orleans.ServiceBus.EventHub.MessagesReceived_{config.Hub.Path}-{config.Partition}";
            hubAgeOfMessagesBeingProcessed = $"Orleans.ServiceBus.EventHub.AgeOfMessagesBeingProcessed_{config.Hub.Path}";
            partitionAgeOfMessagesBeingProcessed = $"Orleans.ServiceBus.EventHub.AgeOfMessagesBeingProcessed_{config.Hub.Path}-{config.Partition}";
        }

        public Task Initialize(TimeSpan timeout)
        {
            logger.Info("Initializing EventHub partition {0}-{1}.", config.Hub.Path, config.Partition);
            // if receiver was already running, do nothing
            return ReceiverRunning == Interlocked.Exchange(ref recieverState, ReceiverRunning) ? TaskDone.Done : Initialize();
        }

        /// <summary>
        /// Initialization of EventHub receiver is performed at adapter reciever initialization, but if it fails,
        ///  it will be retried when messages are requested
        /// </summary>
        /// <returns></returns>
        private async Task Initialize()
        {
            checkpointer = await checkpointerFactory(config.Partition);
            cache = cacheFactory(config.Partition, checkpointer, baseLogger);
            flowController = new AggregatedQueueFlowController(MaxMessagesPerRead) { cache };
            string offset = await checkpointer.Load();
            receiver = await CreateReceiver(config, offset, logger);
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            if (recieverState==ReceiverShutdown || maxCount <= 0)
            {
                return new List<IBatchContainer>();
            }

            // if receiver initialization failed, retry
            if (receiver == null)
            {
                logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead, "Retrying initialization of EventHub partition {0}-{1}.", config.Hub.Path, config.Partition);
                await Initialize();
                if (receiver==null)
                {
                    // should not get here, should throw instead, but just incase.
                    return new List<IBatchContainer>();
                }
            }

            List<EventData> messages;
            try
            {
                var watch = Stopwatch.StartNew();
                messages = (await receiver.ReceiveAsync(maxCount, ReceiveTimeout)).ToList();
                watch.Stop();

                logger.TrackMetric(hubReceiveTimeMetric, watch.Elapsed);
                logger.TrackMetric(partitionReceiveTimeMetric, watch.Elapsed);
                logger.TrackMetric(hubReadFailure, 0);
                logger.TrackMetric(partitionReadFailure, 0);
            }
            catch (Exception ex)
            {
                logger.TrackMetric(hubReadFailure, 1);
                logger.TrackMetric(partitionReadFailure, 1);
                logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead, "Failed to read from EventHub partition {0}-{1}. : Exception: {2}.", config.Hub.Path,
                    config.Partition, ex);
                throw;
            }

            var batches = new List<IBatchContainer>();
            if (messages.Count == 0)
            {
                return batches;
            }

            logger.TrackMetric(hubMessagesRecieved, messages.Count);
            logger.TrackMetric(partitionMessagesReceived, messages.Count);

            // monitor message age
            var dequeueTimeUtc = DateTime.UtcNow;
            TimeSpan difference = dequeueTimeUtc - messages[messages.Count-1].EnqueuedTimeUtc;
            logger.TrackMetric(hubAgeOfMessagesBeingProcessed, difference);
            logger.TrackMetric(partitionAgeOfMessagesBeingProcessed, difference);

            foreach (EventData message in messages)
            {
                StreamPosition streamPosition = cache.Add(message, dequeueTimeUtc);
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
            // if receiver was already shutdown, do nothing
            if (ReceiverShutdown == Interlocked.Exchange(ref recieverState, ReceiverShutdown))
            {
                return TaskDone.Done;
            }

            logger.Info("Stopping reading from EventHub partition {0}-{1}", config.Hub.Path, config.Partition);

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
            localCache?.Dispose();
            // finish return receiver closing task
            return closeTask;
        }

        private static async Task<EventHubReceiver> CreateReceiver(EventHubPartitionConfig partitionConfig, string offset, Logger logger)
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
                logger.Info("Starting to read from EventHub partition {0}-{1} at offset {2}", partitionConfig.Hub.Path, partitionConfig.Partition, offset);
            }
            else
            {
                // to start reading from most recent data, we get the latest offset from the partition.
                PartitionRuntimeInformation patitionInfo =
                    await client.GetPartitionRuntimeInformationAsync(partitionConfig.Partition);
                offset = patitionInfo.LastEnqueuedOffset;
                logger.Info("Starting to read latest messages from EventHub partition {0}-{1} at offset {2}", partitionConfig.Hub.Path, partitionConfig.Partition, offset);
            }
            return await consumerGroup.CreateReceiverAsync(partitionConfig.Partition, offset, true);
        }

        private class StreamActivityNotificationBatch : IBatchContainer
        {
            public Guid StreamGuid { get; }
            public string StreamNamespace { get; }
            public StreamSequenceToken SequenceToken { get; }

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
