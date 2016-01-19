
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ServiceBus.Messaging;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers.Streams.EventHub;
using Orleans.Streams;

namespace OrleansServiceBusUtils.Providers.Streams.EventHub
{
    [Serializable]
    internal class EventHubBatchContainer : IBatchContainer
    {
        [JsonProperty]
        private readonly EventHubSequenceToken token;

        [JsonProperty]
        private readonly byte[] payloadBytes;

        public Guid StreamGuid { get; private set; }
        public string StreamNamespace { get; private set; }
        public StreamSequenceToken SequenceToken { get { return token; } }

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized]
        private Body payload;
        private Body Payload
        {
            get { return payload ?? (payload = SerializationManager.DeserializeFromByteArray<Body>(payloadBytes)); }
        }
        
        [Serializable]
        private class Body
        {
            public List<object> Events { get; set; }
            public Dictionary<string, object> RequestContext { get; set; }
        }

        public EventHubBatchContainer(Guid streamGuid, string streamNamespace, byte[] data)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            payloadBytes = data;
        }

        public EventHubBatchContainer(Guid streamGuid, string streamNamespace, string offset, long sequenceNumber, byte[] data)
            : this(streamGuid, streamNamespace, data)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            token = new EventHubSequenceToken(offset, sequenceNumber, 0);
            payloadBytes = data;
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

        internal static IBatchContainer FromEventData(EventData eventData)
        {
            Guid streamGuid = Guid.Parse(eventData.PartitionKey);
            string streamNamespace = eventData.GetStreamNamespaceProperty();
            byte[] bytes = eventData.GetBytes();
            return new EventHubBatchContainer(streamGuid, streamNamespace, eventData.Offset, eventData.SequenceNumber, bytes);
        }
    }
}
