using System;
using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Converts between Azure Event Hubs messages and Orleans stream cache messages.
    /// </summary>
    public interface IEventHubDataAdapter : IQueueDataAdapter<EventData>, ICacheDataAdapter
    {
        /// <summary>
        /// Converts an Event Hub message into a cached message.
        /// </summary>
        /// <param name="position">The stream position of the message.</param>
        /// <param name="queueMessage">The Event Hub message.</param>
        /// <param name="dequeueTime">The UTC time when the message was dequeued.</param>
        /// <param name="getSegment">The delegate used to allocate a cache segment of the required size.</param>
        /// <returns>The cached message.</returns>
        CachedMessage FromQueueMessage(StreamPosition position, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment);

        /// <summary>
        /// Gets the stream position represented by an Event Hub message.
        /// </summary>
        /// <param name="partition">The Event Hub partition identifier.</param>
        /// <param name="queueMessage">The Event Hub message.</param>
        /// <returns>The stream position.</returns>
        StreamPosition GetStreamPosition(string partition, EventData queueMessage);

        /// <summary>
        /// Gets the Event Hub offset stored in a cached message.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <returns>The Event Hub offset.</returns>
        string GetOffset(CachedMessage cachedMessage);

        /// <summary>
        /// Gets the Event Hub partition key for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The partition key.</returns>
        string GetPartitionKey(StreamId streamId);

        /// <summary>
        /// Gets the Orleans stream identifier represented by an Event Hub message.
        /// </summary>
        /// <param name="queueMessage">The Event Hub message.</param>
        /// <returns>The stream identifier.</returns>
        StreamId GetStreamIdentity(EventData queueMessage);
    }
}
