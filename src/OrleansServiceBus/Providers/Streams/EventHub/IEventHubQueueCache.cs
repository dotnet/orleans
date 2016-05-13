
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
        StreamPosition Add(EventData message, DateTime dequeueTimeUtc);
        object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken);
        bool TryGetNextMessage(object cursorObj, out IBatchContainer message);
    }
}
