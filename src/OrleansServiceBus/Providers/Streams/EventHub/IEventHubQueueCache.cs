
using System;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Interface for a stream message cache that stores EventHub EventData
    /// </summary>
    public interface IEventHubQueueCache : IQueueFlowController, IDisposable
    {
        /// <summary>
        /// Add an EventHub EventData to the cache.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        StreamPosition Add(EventData message, DateTime dequeueTimeUtc);
        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken);
        /// <summary>
        /// Try to get the next message in the cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        bool TryGetNextMessage(object cursorObj, out IBatchContainer message);
    }
}
