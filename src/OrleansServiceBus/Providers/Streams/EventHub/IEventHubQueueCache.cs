
using System;
using Microsoft.ServiceBus.Messaging;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    public interface IEventHubQueueCache : IQueueFlowController, IDisposable
    {
        void Add(EventData message);
        object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken);
        bool TryGetNextMessage(object cursorObj, out IBatchContainer message);
    }
}
