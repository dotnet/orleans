using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Hosting;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Event Hub Partition settings
    /// </summary>
    public class EventHubPartitionSettings
    {
        /// <summary>
        /// Eventhub settings
        /// </summary>
        public EventHubOptions Hub { get; set; }

        public EventHubReceiverOptions ReceiverOptions { get; set; }
        /// <summary>
        /// Partition name
        /// </summary>
        public string Partition { get; set; }
    }

    internal class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public const int MaxMessagesPerRead = 1000;
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionSettings settings;
        private readonly Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, ITelemetryProducer, IEventHubQueueCache> cacheFactory;
        private readonly Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly IQueueAdapterReceiverMonitor monitor;
        private readonly ITelemetryProducer telemetryProducer;
        private readonly LoadSheddingOptions loadSheddingOptions;

        private IEventHubQueueCache cache;

        private IEventHubReceiver receiver;

        private Func<EventHubPartitionSettings, string, ILogger, ITelemetryProducer, Task<IEventHubReceiver>> eventHubReceiverFactory;

        private IStreamQueueCheckpointer<string> checkpointer;
        private AggregatedQueueFlowController flowController;

        // Receiver life cycle
        private int recieverState = ReceiverShutdown;

        private const int ReceiverShutdown = 0;
        private const int ReceiverRunning = 1;

        public int GetMaxAddCount()
        {
            return this.flowController.GetMaxAddCount();
        }

        public EventHubAdapterReceiver(EventHubPartitionSettings settings,
            Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, ITelemetryProducer, IEventHubQueueCache> cacheFactory,
            Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            ILoggerFactory loggerFactory,
            IQueueAdapterReceiverMonitor monitor,
            LoadSheddingOptions loadSheddingOptions,
            ITelemetryProducer telemetryProducer,
            Func<EventHubPartitionSettings, string, ILogger, ITelemetryProducer, Task<IEventHubReceiver>> eventHubReceiverFactory = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (cacheFactory == null) throw new ArgumentNullException(nameof(cacheFactory));
            if (checkpointerFactory == null) throw new ArgumentNullException(nameof(checkpointerFactory));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));
            if (loadSheddingOptions == null) throw new ArgumentNullException(nameof(loadSheddingOptions));
            if (telemetryProducer == null) throw new ArgumentNullException(nameof(telemetryProducer));
            this.settings = settings;
            this.cacheFactory = cacheFactory;
            this.checkpointerFactory = checkpointerFactory;
            this.loggerFactory = loggerFactory;
            this.logger = this.loggerFactory.CreateLogger($"{this.GetType().FullName}.{settings.Hub.Path}.{settings.Partition}");
            this.monitor = monitor;
            this.telemetryProducer = telemetryProducer;
            this.loadSheddingOptions = loadSheddingOptions;

            this.eventHubReceiverFactory = eventHubReceiverFactory == null ? EventHubAdapterReceiver.CreateReceiver : eventHubReceiverFactory;
        }

        public Task Initialize(TimeSpan timeout)
        {
            this.logger.Info("Initializing EventHub partition {0}-{1}.", this.settings.Hub.Path, this.settings.Partition);
            // if receiver was already running, do nothing
            return ReceiverRunning == Interlocked.Exchange(ref this.recieverState, ReceiverRunning)
                ? Task.CompletedTask
                : Initialize();
        }
        /// <summary>
        /// Initialization of EventHub receiver is performed at adapter reciever initialization, but if it fails,
        ///  it will be retried when messages are requested
        /// </summary>
        /// <returns></returns>
        private async Task Initialize()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                this.checkpointer = await this.checkpointerFactory(this.settings.Partition);
                if(this.cache != null)
                {
                    this.cache.Dispose();
                    this.cache = null;
                }
                this.cache = this.cacheFactory(this.settings.Partition, this.checkpointer, this.loggerFactory, this.telemetryProducer);
                this.flowController = new AggregatedQueueFlowController(MaxMessagesPerRead) { this.cache, LoadShedQueueFlowController.CreateAsPercentOfLoadSheddingLimit(this.loadSheddingOptions) };
                string offset = await this.checkpointer.Load();
                this.receiver = await this.eventHubReceiverFactory(this.settings, offset, this.logger, this.telemetryProducer);
                watch.Stop();
                this.monitor?.TrackInitialization(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackInitialization(false, watch.Elapsed, ex);
                throw;
            }
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            if (this.recieverState == ReceiverShutdown || maxCount <= 0)
            {
                return new List<IBatchContainer>();
            }

            // if receiver initialization failed, retry
            if (this.receiver == null)
            {
                this.logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead,
                    "Retrying initialization of EventHub partition {0}-{1}.", this.settings.Hub.Path, this.settings.Partition);
                await Initialize();
                if (this.receiver == null)
                {
                    // should not get here, should throw instead, but just incase.
                    return new List<IBatchContainer>();
                }
            }
            var watch = Stopwatch.StartNew();
            List<EventData> messages;
            try
            {

                messages = (await this.receiver.ReceiveAsync(maxCount, ReceiveTimeout))?.ToList();
                watch.Stop();

                this.monitor?.TrackRead(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackRead(false, watch.Elapsed, ex);
                this.logger.Warn(OrleansServiceBusErrorCode.FailedPartitionRead,
                    "Failed to read from EventHub partition {0}-{1}. : Exception: {2}.", this.settings.Hub.Path,
                    this.settings.Partition, ex);
                throw;
            }

            var batches = new List<IBatchContainer>();
            if (messages == null || messages.Count == 0)
            {
                this.monitor?.TrackMessagesReceived(0, null, null);
                return batches;
            }

            // monitor message age
            var dequeueTimeUtc = DateTime.UtcNow;

            DateTime oldestMessageEnqueueTime = messages[0].SystemProperties.EnqueuedTimeUtc;
            DateTime newestMessageEnqueueTime = messages[messages.Count - 1].SystemProperties.EnqueuedTimeUtc;

            this.monitor?.TrackMessagesReceived(messages.Count, oldestMessageEnqueueTime, newestMessageEnqueueTime);

            List<StreamPosition> messageStreamPositions = this.cache.Add(messages, dequeueTimeUtc);
            foreach (var streamPosition in messageStreamPositions)
            {
                batches.Add(new StreamActivityNotificationBatch(streamPosition.StreamIdentity.Guid,
                    streamPosition.StreamIdentity.Namespace, streamPosition.SequenceToken));
            }
            if (!this.checkpointer.CheckpointExists)
            {
                this.checkpointer.Update(
                    messages[0].SystemProperties.Offset,
                    DateTime.UtcNow);
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

            //if not under pressure, signal the cache to do a time based purge
            //if under pressure, which means consuming speed is less than producing speed, then shouldn't purge, and don't read more message into the cache
            if (!this.IsUnderPressure())
                this.cache.SignalPurge();
            return false;
        }

        public IQueueCacheCursor GetCacheCursor(IStreamIdentity streamIdentity, StreamSequenceToken token)
        {
            return new Cursor(this.cache, streamIdentity, token);
        }

        public bool IsUnderPressure()
        {
            return this.GetMaxAddCount() <= 0;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return Task.CompletedTask;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                // if receiver was already shutdown, do nothing
                if (ReceiverShutdown == Interlocked.Exchange(ref this.recieverState, ReceiverShutdown))
                {
                    return;
                }

                this.logger.Info("Stopping reading from EventHub partition {0}-{1}", this.settings.Hub.Path, this.settings.Partition);

                // clear cache and receiver
                IEventHubQueueCache localCache = Interlocked.Exchange(ref this.cache, null);

                var localReceiver = Interlocked.Exchange(ref this.receiver, null);

                // start closing receiver
                Task closeTask = Task.CompletedTask;
                if (localReceiver != null)
                {
                    closeTask = localReceiver.CloseAsync();
                }
                // dispose of cache
                localCache?.Dispose();

                // finish return receiver closing task
                await closeTask;
                watch.Stop();
                this.monitor?.TrackShutdown(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackShutdown(false, watch.Elapsed, ex);
                throw;
            }
        }

        private static async Task<IEventHubReceiver> CreateReceiver(EventHubPartitionSettings partitionSettings, string offset, ILogger logger, ITelemetryProducer telemetryProducer)
        {
            bool offsetInclusive = true;
            var connectionStringBuilder = new EventHubsConnectionStringBuilder(partitionSettings.Hub.ConnectionString)
            {
                EntityPath = partitionSettings.Hub.Path
            };
            EventHubClient client = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());

            // if we have a starting offset or if we're not configured to start reading from utc now, read from offset
            if (!partitionSettings.ReceiverOptions.StartFromNow ||
                offset != EventHubConstants.StartOfStream)
            {
                logger.Info("Starting to read from EventHub partition {0}-{1} at offset {2}", partitionSettings.Hub.Path, partitionSettings.Partition, offset);
            }
            else
            {
                // to start reading from most recent data, we get the latest offset from the partition.
                EventHubPartitionRuntimeInformation partitionInfo =
                    await client.GetPartitionRuntimeInformationAsync(partitionSettings.Partition);
                offset = partitionInfo.LastEnqueuedOffset;
                offsetInclusive = false;
                logger.Info("Starting to read latest messages from EventHub partition {0}-{1} at offset {2}", partitionSettings.Hub.Path, partitionSettings.Partition, offset);
            }

            PartitionReceiver receiver = client.CreateReceiver(partitionSettings.Hub.ConsumerGroup, partitionSettings.Partition, offset, offsetInclusive);

            if (partitionSettings.ReceiverOptions.PrefetchCount.HasValue)
                receiver.PrefetchCount = partitionSettings.ReceiverOptions.PrefetchCount.Value;

            return new EventHubReceiverProxy(receiver);
        }

        /// <summary>
        /// For test purpose. ConfigureDataGeneratorForStream will configure a data generator for the stream
        /// </summary>
        /// <param name="streamId"></param>
        internal void ConfigureDataGeneratorForStream(IStreamIdentity streamId)
        {
            (this.receiver as EventHubPartitionGeneratorReceiver)?.ConfigureDataGeneratorForStream(streamId);
        }

        internal void StopProducingOnStream(IStreamIdentity streamId)
        {
            (this.receiver as EventHubPartitionGeneratorReceiver)?.StopProducingOnStream(streamId);
        }

        private class StreamActivityNotificationBatch : IBatchContainer
        {
            public Guid StreamGuid { get; }
            public string StreamNamespace { get; }
            public StreamSequenceToken SequenceToken { get; }

            public StreamActivityNotificationBatch(Guid streamGuid, string streamNamespace,
                StreamSequenceToken sequenceToken)
            {
                this.StreamGuid = streamGuid;
                this.StreamNamespace = streamNamespace;
                this.SequenceToken = sequenceToken;
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
                this.cursor = cache.GetCursor(streamIdentity, token);
            }

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null;
                return this.current;
            }

            public bool MoveNext()
            {
                IBatchContainer next;
                if (!this.cache.TryGetNextMessage(this.cursor, out next))
                {
                    return false;
                }

                this.current = next;
                return true;
            }

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }
    }
}