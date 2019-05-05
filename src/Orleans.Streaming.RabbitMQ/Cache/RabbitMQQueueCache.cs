using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    // undone (mxplusb): implement the rabbit queue cache.
    public abstract class RabbitMQQueueCache<TCachedMessage> : IRabbitMQQueueCache where TCachedMessage : struct
    {
        /// <summary>
        /// Default max number of items that can be added to the cache between purge calls
        /// </summary>
        protected readonly int defaultMaxAddCount;

        /// <summary>
        /// Underlying message cache implementation
        /// </summary>
        protected readonly PooledQueueCache<byte[], TCachedMessage> cache;
        private IEvictionStrategy<TCachedMessage> evictionStrategy;
        private ICacheMonitor cacheMonitor;

        /// <summary>
        /// Logic used to store queue position
        /// </summary>
        protected IStreamQueueCheckpointer<string> Checkpointer { get; }

        protected RabbitMQQueueCache(int defaultMaxAddCount, IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<byte[], TCachedMessage> cacheDataAdapter,
            ICacheDataComparer<TCachedMessage> comparer, ILogger logger, IEvictionStrategy<TCachedMessage> evictionStrategy,
            ICacheMonitor cacheMonitor, TimeSpan? cacheMonitorWriteInterval)
        {
            this.defaultMaxAddCount = defaultMaxAddCount;
            Checkpointer = checkpointer;
            cache = new PooledQueueCache<byte[], TCachedMessage>(cacheDataAdapter, comparer, logger, cacheMonitor, cacheMonitorWriteInterval);
            this.cacheMonitor = cacheMonitor;
            this.evictionStrategy = evictionStrategy;
            this.evictionStrategy.OnPurged = this.OnPurge;
            EvictionStrategyCommonUtils.WireUpEvictionStrategy<byte[], TCachedMessage>(this.cache, cacheDataAdapter, this.evictionStrategy);
        }

        /// <inheritdoc />
        public void SignalPurge()
        {
            this.evictionStrategy.PerformPurge(DateTime.UtcNow);
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
            this.evictionStrategy.OnPurged = null;
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

        // not sure what to do for this one.
        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Add a list of RabbitMQ message payloads to the cache.
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public List<StreamPosition> Add(List<byte[]> messages, DateTime dequeueTimeUtc)
        {
            return cache.Add(messages, dequeueTimeUtc);
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
            return true;
        }
    }

    
}
