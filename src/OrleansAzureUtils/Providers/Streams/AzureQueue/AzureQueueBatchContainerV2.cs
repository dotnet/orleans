
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Second version of AzureQueueBatchContainer.  This version supports external serializers (like json)
    /// </summary>
    [Serializable]
    internal class AzureQueueBatchContainerV2 : IBatchContainer
    {
        [JsonProperty]
        private EventSequenceTokenV2 sequenceToken;

        [JsonProperty]
        private readonly List<object> events;

        [JsonProperty]
        private readonly Dictionary<string, object> requestContext;

        public Guid StreamGuid { get; }

        public String StreamNamespace { get; }

        public StreamSequenceToken SequenceToken => sequenceToken;

        internal EventSequenceTokenV2 RealSequenceToken
        {
            set { sequenceToken = value; }
        }

        [JsonConstructor]
        public AzureQueueBatchContainerV2(
            Guid streamGuid,
            String streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceTokenV2 sequenceToken)
            : this(streamGuid, streamNamespace, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        public AzureQueueBatchContainerV2(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException(nameof(events), "Message contains no events");

            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            this.events = events;
            this.requestContext = requestContext;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, sequenceToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            foreach (object item in events)
            {
                if (shouldReceiveFunc(stream, filterData, item))
                    return true; // There is something in this batch that the consumer is intereted in, so we should send it.
            }
            return false; // Consumer is not interested in any of these events, so don't send.
        }

        public bool ImportRequestContext()
        {
            if (requestContext != null)
            {
                RequestContext.Import(requestContext);
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            return $"[AzureQueueBatchContainerV2:Stream={StreamGuid},#Items={events.Count}]";
        }
    }
}
