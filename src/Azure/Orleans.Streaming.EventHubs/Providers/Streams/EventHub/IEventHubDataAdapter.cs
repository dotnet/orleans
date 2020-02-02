using System;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    public interface IEventHubDataAdapter : IQueueDataAdapter<EventData>, ICacheDataAdapter
    {
        CachedMessage FromQueueMessage(StreamPosition position, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment);

        StreamPosition GetStreamPosition(string partition, EventData queueMessage);

        string GetOffset(CachedMessage cachedMessage);
    }
}
