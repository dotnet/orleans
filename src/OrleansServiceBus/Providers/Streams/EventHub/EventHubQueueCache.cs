
using System;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// EventHub queue cache that allows developers to provide their own cached data structure.
    /// </summary>
    /// <typeparam name="TCachedMessage"></typeparam>
    public abstract class EventHubQueueCache<TCachedMessage> : IEventHubQueueCache
        where TCachedMessage : struct
    {
        /// <summary>
        /// Default max number of items that can be added to the cache between purge calls
        /// </summary>
        protected readonly int defaultMaxAddCount;
        /// <summary>
        /// Underlying message cache implementation
        /// </summary>
        protected readonly PooledQueueCache<EventData, TCachedMessage> cache;
        private readonly AveragingCachePressureMonitor cachePressureMonitor;

        /// <summary>
        /// Logic used to store queue position
        /// </summary>
        protected IStreamQueueCheckpointer<string> Checkpointer { get; }

        /// <summary>
        /// Construct EventHub queue cache.
        /// </summary>
        /// <param name="defaultMaxAddCount">Default max number of items that can be added to the cache between purge calls.</param>
        /// <param name="checkpointer">Logic used to store queue position.</param>
        /// <param name="cacheDataAdapter">Performs data transforms appropriate for the various types of queue data.</param>
        /// <param name="comparer">Compares cached data</param>
        /// <param name="logger"></param>
        protected EventHubQueueCache(int defaultMaxAddCount, IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, TCachedMessage> cacheDataAdapter, ICacheDataComparer<TCachedMessage> comparer, Logger logger)
        {
            this.defaultMaxAddCount = defaultMaxAddCount;
            Checkpointer = checkpointer;
            cache = new PooledQueueCache<EventData, TCachedMessage>(cacheDataAdapter, comparer, logger);
            cacheDataAdapter.PurgeAction = cache.Purge;
            cache.OnPurged = OnPurge;

            cachePressureMonitor = new AveragingCachePressureMonitor(logger);
        }

        /// <summary>
        /// Get offset from cached message.  Left to derived class, as only it knows how to get this from the cached message.
        /// </summary>
        /// <param name="lastItemPurged"></param>
        /// <returns></returns>
        protected abstract string GetOffset(TCachedMessage lastItemPurged);
        
        /// <summary>
        /// cachePressureContribution should be a double between 0-1, indicating how much danger the item is of being removed from the cache.
        ///   0 indicating  no danger,
        ///   1 indicating removal is imminent.
        /// </summary>
        /// <param name="token"></param>
        /// <param name="cachePressureContribution"></param>
        /// <returns></returns>
        protected abstract bool TryCalculateCachePressureContribution(StreamSequenceToken token, out double cachePressureContribution);

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            cache.OnPurged = null;
        }

        /// <summary>
        /// Handles cache purge signals
        /// </summary>
        /// <param name="lastItemPurged"></param>
        /// <param name="newestItem"></param>
        protected virtual void OnPurge(TCachedMessage? lastItemPurged, TCachedMessage? newestItem)
        {
            if (lastItemPurged.HasValue)
            {
                UpdateCheckpoint(lastItemPurged.Value);
            }
        }

        private void UpdateCheckpoint(TCachedMessage lastItemPurged)
        {
            Checkpointer.Update(GetOffset(lastItemPurged), DateTime.UtcNow);
        }

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount()
        {
            return cachePressureMonitor.IsUnderPressure(DateTime.UtcNow) ? 0 : defaultMaxAddCount;
        }

        /// <summary>
        /// Add an EventHub EventData to the cache.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public StreamPosition Add(EventData message, DateTime dequeueTimeUtc)
        {
            return cache.Add(message, dequeueTimeUtc);
        }

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            return cache.GetCursor(streamIdentity, sequenceToken);
        }

        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            if (!cache.TryGetNextMessage(cursorObj, out message))
                return false;
            double cachePressureContribution;
            cachePressureMonitor.RecordCachePressureContribution(
                TryCalculateCachePressureContribution(message.SequenceToken, out cachePressureContribution)
                    ? cachePressureContribution
                    : 0.0);
            return true;
        }

    }

    internal class AveragingCachePressureMonitor
    {
        private static readonly TimeSpan checkPeriod = TimeSpan.FromSeconds(2);
        const double pressureThreshold = 1.0 / 3.0;
        private readonly Logger logger;

        private double accumulatedCachePressure;
        private double cachePressureContributionCount;
        private DateTime nextCheckedTime;
        private bool isUnderPressure;

        public AveragingCachePressureMonitor(Logger logger)
        {
            this.logger = logger.GetSubLogger("flowcontrol", "-");
            nextCheckedTime = DateTime.MinValue;
            isUnderPressure = false;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            // Weight unhealthy contributions thrice as much as healthy ones.
            // This is a crude compensation for the fact that healthy consumers wil consume more often than unhealthy ones.
            double weight = cachePressureContribution < pressureThreshold ? 1.0 : 3.0;
            accumulatedCachePressure += cachePressureContribution * weight;
            cachePressureContributionCount += weight;
        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            if (nextCheckedTime < utcNow)
            {
                CalculatePressure();
                nextCheckedTime = utcNow + checkPeriod;
            }
            return isUnderPressure;
        }

        private void CalculatePressure()
        {
            // if we don't have any contributions, don't change status
            if (cachePressureContributionCount < 0.5)
            {
                // after 5 checks with no contributions, check anyway
                cachePressureContributionCount += 0.1;
                return;
            }

            double pressure = accumulatedCachePressure / cachePressureContributionCount;
            bool wasUnderPressure = isUnderPressure;
            isUnderPressure = pressure > pressureThreshold;
            // If we changed state, log
            if (isUnderPressure != wasUnderPressure)
            {
                logger.Info(isUnderPressure
                    ? $"Ingesting messages too fast. Throttling message reading. AccumulatedCachePressure: {accumulatedCachePressure}, Contributions: {cachePressureContributionCount}, AverageCachePressure: {pressure}, Threshold: {pressureThreshold}"
                    : $"Message ingestion is healthy. AccumulatedCachePressure: {accumulatedCachePressure}, Contributions: {cachePressureContributionCount}, AverageCachePressure: {pressure}, Threshold: {pressureThreshold}");
            }
            cachePressureContributionCount = 0.0;
            accumulatedCachePressure = 0.0;
        }
    }

    /// <summary>
    /// Message cache that stores EventData as a CachedEventHubMessage in a pooled message cache
    /// </summary>
    public class EventHubQueueCache : EventHubQueueCache<CachedEventHubMessage>
    {
        private readonly Logger log;

        /// <summary>
        /// Construct cache given a buffer pool.  Will use default data adapter
        /// </summary>
        /// <param name="checkpointer"></param>
        /// <param name="bufferPool"></param>
        /// <param name="timePurge"></param>
        /// <param name="logger"></param>
        public EventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge, Logger logger)
            : this(checkpointer, new EventHubDataAdapter(bufferPool, timePurge), logger)
        {
        }

        /// <summary>
        /// Construct cache given a custom data adapter.
        /// </summary>
        /// <param name="checkpointer"></param>
        /// <param name="cacheDataAdapter"></param>
        /// <param name="logger"></param>
        public EventHubQueueCache(IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter, Logger logger)
            : base(EventHubAdapterReceiver.MaxMessagesPerRead, checkpointer, cacheDataAdapter, EventHubDataComparer.Instance, logger)
        {
            log = logger.GetSubLogger("messagecache", "-");
        }

        /// <summary>
        /// Handles cache purge signals
        /// </summary>
        /// <param name="lastItemPurged"></param>
        /// <param name="newestItem"></param>
        protected override void OnPurge(CachedEventHubMessage? lastItemPurged, CachedEventHubMessage? newestItem)
        {
            if (log.IsVerbose && lastItemPurged.HasValue && newestItem.HasValue)
            {
                log.Verbose($"CachePeriod: EnqueueTimeUtc: {LogFormatter.PrintDate(lastItemPurged.Value.EnqueueTimeUtc)} to {LogFormatter.PrintDate(newestItem.Value.EnqueueTimeUtc)}, DequeueTimeUtc: {LogFormatter.PrintDate(lastItemPurged.Value.DequeueTimeUtc)} to {LogFormatter.PrintDate(newestItem.Value.DequeueTimeUtc)}");
            }
        }

        /// <summary>
        /// Get offset from cached message.  Left to derived class, as only it knows how to get this from the cached message.
        /// </summary>
        /// <param name="lastItemPurged"></param>
        /// <returns></returns>
        protected override string GetOffset(CachedEventHubMessage lastItemPurged)
        {
            // TODO figure out how to get this from the adapter
            int readOffset = 0;
            SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read namespace, not needed so throw away.
            return SegmentBuilder.ReadNextString(lastItemPurged.Segment, ref readOffset); // read offset
        }

        /// <summary>
        /// cachePressureContribution should be a double between 0-1, indicating how much danger the item is of being removed from the cache.
        ///   0 indicating  no danger,
        ///   1 indicating removal is imminent.
        /// </summary>
        /// <param name="token"></param>
        /// <param name="cachePressureContribution"></param>
        /// <returns></returns>
        protected override bool TryCalculateCachePressureContribution(StreamSequenceToken token, out double cachePressureContribution)
        {
            cachePressureContribution = 0;
            // if cache is empty or has few items, don't calculate pressure
            if (cache.IsEmpty ||
                !cache.Newest.HasValue ||
                !cache.Oldest.HasValue ||
                cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber < 10*defaultMaxAddCount) // not enough items in cache.
            {
                return false;
            }

            IEventHubPartitionLocation location = (IEventHubPartitionLocation) token;
            double cacheSize = cache.Newest.Value.SequenceNumber - cache.Oldest.Value.SequenceNumber;
            long distanceFromNewestMessage = cache.Newest.Value.SequenceNumber - location.SequenceNumber;

            // pressure is the ratio of the distance from the front of the cache to the 
            cachePressureContribution = distanceFromNewestMessage/cacheSize;

            return true;
        }
    }
}
