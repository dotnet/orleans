using System;
using System.Collections.Generic;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Default event hub data adapter.  Users may subclass to override event data to stream mapping.
    /// </summary>
    public class EventHubDataAdapter : IQueueMessageCacheAdapterFactory<EventData>, IQueueDataAdapter<EventData>, ICacheDataAdapter
    {
        protected readonly SerializationManager serializationManager;

        /// <summary>
        /// Cache data adapter that adapts EventHub's EventData to CachedEventHubMessage used in cache
        /// </summary>
        public EventHubDataAdapter(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        public virtual IBatchContainer GetBatchContainer(in CachedMessage cachedMessage)
        {
            var evenHubMessage = new EventHubMessage(cachedMessage, this.serializationManager);
            return new EventHubBatchContainer(evenHubMessage, this.serializationManager);
        }

        public virtual EventData ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            if (token != null) throw new ArgumentException("EventHub streams currently does not support non-null StreamSequenceToken.", nameof(token));
            return EventHubBatchContainer.ToEventData(this.serializationManager, streamGuid, streamNamespace, events, requestContext);
        }

        public virtual IQueueMessageCacheAdapter Create(string partition, EventData queueMessage)
            => new EventDataCacheAdapter(queueMessage, this.serializationManager);

        // these conversions must always be used and can't be customized, as other systems depend on them
        public static byte[] OffsetToToken(string offset) => System.Text.Encoding.ASCII.GetBytes(offset);
        public static string TokenToOffset(byte[] offsetToken) => System.Text.Encoding.ASCII.GetString(offsetToken);
    }
}
