
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ServiceBus.Messaging;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Batch container that is delivers payload and stream position information for a set of events in an EventHub EventData.
    /// </summary>
    [Serializable]
    public class EventHubBatchContainer : IBatchContainer
    {
        [JsonProperty]
        private readonly EventHubMessage eventHubMessage;

        [JsonProperty]
        private readonly EventHubSequenceToken token;

        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        public Guid StreamGuid => eventHubMessage.StreamIdentity.Guid;

        /// <summary>
        /// Stream namespace for the stream this batch is part of.
        /// </summary>
        public string StreamNamespace => eventHubMessage.StreamIdentity.Namespace;

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        public StreamSequenceToken SequenceToken => token;

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized]
        private Body payload;
        private Body Payload => payload ?? (payload = SerializationManager.DeserializeFromByteArray<Body>(eventHubMessage.Payload));

        [Serializable]
        private class Body
        {
            public List<object> Events { get; set; }
            public Dictionary<string, object> RequestContext { get; set; }
        }

        /// <summary>
        /// Batch container that deliveres events from cached EventHub data associated with an orleans stream
        /// </summary>
        /// <param name="eventHubMessage"></param>
        public EventHubBatchContainer(EventHubMessage eventHubMessage)
        {
            this.eventHubMessage = eventHubMessage;
            token = new EventHubSequenceTokenV2(eventHubMessage.Offset, eventHubMessage.SequenceNumber, 0);
        }

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return Payload.Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, new EventHubSequenceTokenV2(token.EventHubOffset, token.SequenceNumber, i)));
        }

        /// <summary>
        /// Gives an opportunity to IBatchContainer to set any data in the RequestContext before this IBatchContainer is sent to consumers.
        /// It can be the data that was set at the time event was generated and enqueued into the persistent provider or any other data.
        /// </summary>
        /// <returns>True if the RequestContext was indeed modified, false otherwise.</returns>
        public bool ImportRequestContext()
        {
            if (Payload.RequestContext != null)
            {
                RequestContext.Import(Payload.RequestContext);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Decide whether this batch should be sent to the specified target.
        /// </summary>
        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            return true;
        }

        internal static EventData ToEventData<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var payload = new Body
            {
                Events = events.Cast<object>().ToList(),
                RequestContext = requestContext
            };
            var bytes = SerializationManager.SerializeToByteArray(payload);
            var eventData = new EventData(bytes) { PartitionKey = streamGuid.ToString() };
            if (!string.IsNullOrWhiteSpace(streamNamespace))
            {
                eventData.SetStreamNamespaceProperty(streamNamespace);
            }
            return eventData;
        }
    }
}
