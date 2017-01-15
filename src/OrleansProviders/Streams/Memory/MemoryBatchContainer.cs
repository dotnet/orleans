

using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers
{
    [Serializable]
    internal class MemoryBatchContainer<TSerializer> : IBatchContainer
        where TSerializer : IMemoryMessageBodySerializer, new()
    {
        private readonly EventSequenceToken realToken;
        public Guid StreamGuid => MessageData.StreamGuid;
        public string StreamNamespace => MessageData.StreamNamespace;
        public StreamSequenceToken SequenceToken => realToken;
        public MemoryMessageData MessageData { get; set; }
        public long SequenceNumber => realToken.SequenceNumber;

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized] private MemoryMessageBody payload;
        [NonSerialized] private IMemoryMessageBodySerializer serializer;

        private MemoryMessageBody Payload()
        {
            if (serializer == null)
            {
                serializer = new TSerializer();
            }
            return payload ?? (payload = serializer.Deserialize(MessageData.Payload));
        }

        public MemoryBatchContainer(MemoryMessageData messageData)
        {
            serializer = new TSerializer();
            MessageData = messageData;
            realToken = new EventSequenceToken(messageData.SequenceNumber);
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return Payload().Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, realToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ImportRequestContext()
        {
            var context = Payload().RequestContext;
            if (context != null)
            {
                RequestContext.Import(context);
                return true;
            }
            return false;
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            return true;
        }
    }
}
