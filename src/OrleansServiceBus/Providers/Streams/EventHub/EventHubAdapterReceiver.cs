
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
    internal class EventHubPartitionSettings
    {
        public IEventHubSettings Hub { get; set; }
        public string Partition { get; set; }
    }

    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public const int MaxMessagesPerRead = 1000;
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionSettings settings;
        private readonly Func<string, IStreamQueueCheckpointer<string>, Logger, IEventHubQueueCache> cacheFactory;
        private readonly Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory;
        private readonly Logger baseLogger;
        private readonly Logger logger;
        private readonly IEventHubReceiverMonitor monitor;

        private IEventHubQueueCache cache;
        private EventHubReceiver receiver;
        private IStreamQueueCheckpointer<string> checkpointer;
        private AggregatedQueueFlowController flowController;

        // Receiver life cycle
        private int recieverState = ReceiverShutdown;
        private const int ReceiverShutdown = 0;
        private const int ReceiverRunning = 1;

        public int GetMaxAddCount() { return flowController.GetMaxAddCount(); }

        public EventHubAdapterReceiver(EventHubPartitionSettings settings,
            Func<string, IStreamQueueCheckpointer<string>, Logger,IEventHubQueueCache> cacheFactory,
            Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            Logger baseLogger,
            IEventHubReceiverMonitor monitor = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (cacheFactory == null) throw new ArgumentNullException(nameof(cacheFactory));
            if (checkpointerFactory == null) throw new ArgumentNullException(nameof(checkpointerFactory));
            if (baseLogger == null) throw new ArgumentNullException(nameof(baseLogger));
            this.settings = settings;
            this.cacheFactory = cacheFactory;
            this.checkpointerFactory = checkpointerFactory;
            this.baseLogger = baseLogger;
            this.logger = baseLogger.GetSubLogger("receiver", "-");
            this.monitor = monitor ?? new DefaultEventHubReceiverMonitor(settings.Hub.Path, settings.Partition, baseLogger.GetSubLogger("monitor", "-"));
        }

        public Task Initialize(TimeSpan timeout)
        {
            logger.Info("Initializing EventHub partition {0}-{1}.", settings.Hub.Path, settings.Partition);
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
            checkpointer = await checkpointerFactory(settings.Partition);
            cache = cacheFactory(settings.Partition, checkpointer, baseLogger);
            flowController = new AggregatedQueueFlowController(MaxMessagesPerRead) { cache };
            string offset = await checkpointer.Load();
            receiver = await CreateReceiver(settings, offset, logger);
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
                logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead, "Retrying initialization of EventHub partition {0}-{1}.", settings.Hub.Path, settings.Partition);
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

                monitor.TrackRead(true);
                monitor.TrackMessagesRecieved(messages.Count, watch.Elapsed);
            }
            catch (Exception ex)
            {
                monitor.TrackRead(false);
                logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead, "Failed to read from EventHub partition {0}-{1}. : Exception: {2}.", settings.Hub.Path,
                    settings.Partition, ex);
                throw;
            }

            var batches = new List<IBatchContainer>();
            if (messages.Count == 0)
            {
                return batches;
            }

            // monitor message age
            var dequeueTimeUtc = DateTime.UtcNow;
            TimeSpan oldest = dequeueTimeUtc - messages[0].EnqueuedTimeUtc;
            TimeSpan newest = dequeueTimeUtc - messages[messages.Count - 1].EnqueuedTimeUtc;
            monitor.TrackAgeOfMessagesRead(oldest, newest);

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

            logger.Info("Stopping reading from EventHub partition {0}-{1}", settings.Hub.Path, settings.Partition);

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

        private static async Task<EventHubReceiver> CreateReceiver(EventHubPartitionSettings partitionSettings, string offset, Logger logger)
        {
            bool offsetInclusive = true;
            EventHubClient client = EventHubClient.CreateFromConnectionString(partitionSettings.Hub.ConnectionString, partitionSettings.Hub.Path);
            EventHubConsumerGroup consumerGroup = client.GetConsumerGroup(partitionSettings.Hub.ConsumerGroup);
            if (partitionSettings.Hub.PrefetchCount.HasValue)
            {
                consumerGroup.PrefetchCount = partitionSettings.Hub.PrefetchCount.Value;
            }
            // if we have a starting offset or if we're not configured to start reading from utc now, read from offset
            if (!partitionSettings.Hub.StartFromNow || offset != EventHubConsumerGroup.StartOfStream)
            {
                logger.Info("Starting to read from EventHub partition {0}-{1} at offset {2}", partitionSettings.Hub.Path, partitionSettings.Partition, offset);
            }
            else
            {
                // to start reading from most recent data, we get the latest offset from the partition.
                PartitionRuntimeInformation patitionInfo =
                    await client.GetPartitionRuntimeInformationAsync(partitionSettings.Partition);
                offset = patitionInfo.LastEnqueuedOffset;
                offsetInclusive = false;
                logger.Info("Starting to read latest messages from EventHub partition {0}-{1} at offset {2}", partitionSettings.Hub.Path, partitionSettings.Partition, offset);
            }
            return await consumerGroup.CreateReceiverAsync(partitionSettings.Partition, offset, offsetInclusive);
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
