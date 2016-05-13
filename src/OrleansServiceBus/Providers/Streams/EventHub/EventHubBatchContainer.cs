﻿
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

        public Guid StreamGuid => eventHubMessage.StreamIdentity.Guid;
        public string StreamNamespace => eventHubMessage.StreamIdentity.Namespace;
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

        public EventHubBatchContainer(EventHubMessage eventHubMessage)
        {
            this.eventHubMessage = eventHubMessage;
            token = new EventHubSequenceToken(eventHubMessage.Offset, eventHubMessage.SequenceNumber, 0);
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return Payload.Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, new EventHubSequenceToken(token.EventHubOffset, token.SequenceNumber, i)));
        }

        public bool ImportRequestContext()
        {
            if (Payload.RequestContext != null)
            {
                RequestContext.Import(Payload.RequestContext);
                return true;
            }
            return false;
        }

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
