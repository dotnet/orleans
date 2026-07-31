using System;
using Azure.Messaging.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs
{
    public interface IEventHubDataAdapter : IQueueDataAdapter<EventData>, ICacheDataAdapter
    {
        CachedMessage FromQueueMessage(StreamPosition position, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment);

        StreamPosition GetStreamPosition(string partition, EventData queueMessage);

        string GetOffset(CachedMessage cachedMessage);

        string GetPartitionKey(StreamId streamId);

        StreamId GetStreamIdentity(EventData queueMessage);
    }
}
