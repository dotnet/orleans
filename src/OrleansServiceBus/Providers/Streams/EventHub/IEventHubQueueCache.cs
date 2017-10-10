
using System;
#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using System.Collections.Generic;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Interface for a stream message cache that stores EventHub EventData
    /// </summary>
    public interface IEventHubQueueCache : IQueueFlowController, IDisposable
    {
        /// <summary>
        /// Add a list of EventHub EventData to the cache.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc);

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

        /// <summary>
        /// Add cache pressure monitor to the cache's back pressure algorithm
        /// </summary>
        /// <param name="monitor"></param>
        void AddCachePressureMonitor(ICachePressureMonitor monitor);

        /// <summary>
        /// Send purge signal to the cache, the cache will perform a time based purge on its cached messages
        /// </summary>
        void SignalPurge();
    }
}
