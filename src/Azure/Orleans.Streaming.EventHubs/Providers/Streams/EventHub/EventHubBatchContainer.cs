using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Messaging.EventHubs;
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
    [GenerateSerializer]
    public class EventHubBatchContainer : IBatchContainer
    {
        [JsonProperty]
        [Id(0)]
        private readonly EventHubMessage eventHubMessage;

        [JsonIgnore]
        [field: NonSerialized]
        internal Serializer Serializer { get; set; }

        [JsonProperty]
        [Id(1)]
        private readonly EventHubSequenceToken token;

        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        public StreamId StreamId => eventHubMessage.StreamId;

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        public StreamSequenceToken SequenceToken => token;

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized]
        private Body payload;

        private Body GetPayload() => payload ?? (payload = this.Serializer.Deserialize<Body>(eventHubMessage.Payload));

        [Serializable]
        [GenerateSerializer]
        internal class Body
        {
            [Id(0)]
            public List<object> Events { get; set; }
            [Id(1)]
            public Dictionary<string, object> RequestContext { get; set; }
        }

        /// <summary>
        /// Batch container that delivers events from cached EventHub data associated with an orleans stream
        /// </summary>
        public EventHubBatchContainer(EventHubMessage eventHubMessage, Serializer serializer)
        {
            this.eventHubMessage = eventHubMessage;
            this.Serializer = serializer;
            token = new EventHubSequenceTokenV2(eventHubMessage.Offset, eventHubMessage.SequenceNumber, 0);
        }

        [GeneratedActivatorConstructor]
        internal EventHubBatchContainer(Serializer serializer)
        {
            this.Serializer = serializer;
        }

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return GetPayload().Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, new EventHubSequenceTokenV2(token.EventHubOffset, token.SequenceNumber, i)));
        }

        /// <summary>
        /// Gives an opportunity to IBatchContainer to set any data in the RequestContext before this IBatchContainer is sent to consumers.
        /// It can be the data that was set at the time event was generated and enqueued into the persistent provider or any other data.
        /// </summary>
        /// <returns>True if the RequestContext was indeed modified, false otherwise.</returns>
        public bool ImportRequestContext()
        {
            if (GetPayload().RequestContext != null)
            {
                RequestContextExtensions.Import(GetPayload().RequestContext);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Put events list and its context into a EventData object
        /// </summary>
        public static EventData ToEventData<T>(Serializer bodySerializer, StreamId streamId, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var payload = new Body
            {
                Events = events.Cast<object>().ToList(),
                RequestContext = requestContext
            };
            var bytes = bodySerializer.SerializeToArray(payload);
            var eventData = new EventData(bytes);

            eventData.SetStreamNamespaceProperty(streamId.GetNamespace());
            return eventData;
        }
    }
}
