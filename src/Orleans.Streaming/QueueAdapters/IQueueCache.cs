using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public interface IQueueCache : IQueueFlowController
    {
        /// <summary>
        /// Adds messages to the cache.
        /// </summary>
        /// <param name="messages">The message batches.</param>
        void AddToCache(IList<IBatchContainer> messages);

        /// <summary>
        /// Requests that the cache purge any items that can be purged.
        /// </summary>
        /// <param name="purgedItems">The purged items.</param>
        /// <returns><see langword="true" /> if items were successfully purged from the cache., <see langword="false" /> otherwise.</returns>
        bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems);

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retrieve messages from the
        /// cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="token">The token.</param>
        /// <returns>The queue cache cursor.</returns>
        IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token);

        /// <summary>
        /// Returns <see langword="true" /> if this cache is under pressure, <see langword="false" /> otherwise.
        /// </summary>
        /// <returns><see langword="true" /> if this cache is under pressure; otherwise, <see langword="false" />.</returns>
        bool IsUnderPressure();
    }
}
