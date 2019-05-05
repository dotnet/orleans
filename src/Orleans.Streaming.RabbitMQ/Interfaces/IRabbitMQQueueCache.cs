using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Interface for a stream message cache that can store message data for RabbitMQ.
    /// </summary>
    public interface IRabbitMQQueueCache : IQueueFlowController, IDisposable
    {
        /// <summary>
        /// Add a list of messages to the cache.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        List<StreamPosition> Add(List<byte[]> message, DateTime dequeueTimeUtc);

        /// <summary>
        /// Get a cursor into the cache to read events from a stream.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken);

        /// <summary>
        /// Try to get the next message in teh cache for the provided cursor.
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        bool TryGetNextMessage(object cursorObj, out IBatchContainer message);

        /// <summary>
        /// Send a purge signal to the cache, the cache will perform a time-based purge on it's cached messages.
        /// </summary>
        void SignalPurge();
    }
}
