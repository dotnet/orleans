using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Memory
{
    [Serializable]
    internal class MemoryBatchContainer : IBatchContainer
    {
        private readonly EventSequenceToken realToken;
        public Guid StreamGuid => EventData.StreamGuid;
        public string StreamNamespace => EventData.StreamNamespace;
        public StreamSequenceToken SequenceToken => realToken;
        public MemoryEventData EventData { get; set; }
        public long SequenceNumber{ get { return realToken.SequenceNumber; } }

        public MemoryBatchContainer(MemoryEventData eventData, long sequenceId)
        {
            this.EventData = eventData;
            this.realToken = new EventSequenceToken(sequenceId);
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return EventData.Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, realToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ImportRequestContext()
        {
            return false;
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            return EventData?.Events?.Count > 0;
        }

        internal static MemoryEventData ToMemoryEventData<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            MemoryEventData eventData = new MemoryEventData(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            return eventData;
        }

        internal static MemoryBatchContainer FromMemoryEventData<T>(MemoryEventData eventData, long sequenceId)
        {
            return new MemoryBatchContainer(eventData, sequenceId);
        }
    }
}
