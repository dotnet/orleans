using System;
using System.Collections.Generic;

namespace Orleans.Streams
{
    public interface IQueueCache
    {
        /// <summary>
        /// The limit of the maximum number of items that can be added to the cache in a single AddToCache operation.
        /// </summary>
        int MaxAddCount { get; }

        /// <summary>
        /// Add messages to the cache
        /// </summary>
        /// <param name="messages"></param>
        void AddToCache(IList<IBatchContainer> messages);

        /// <summary>
        /// Ask the cache if it has items that can be purged from the cache 
        /// (so that they can be subsequently released them the underlying queue).
        /// </summary>
        /// <param name="purgedItems"></param>
        bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems);

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retreave messages from the
        ///   cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamGuid"></param>
        /// <param name="streamNamespace"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token);

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        bool IsUnderPressure();
    }
}
