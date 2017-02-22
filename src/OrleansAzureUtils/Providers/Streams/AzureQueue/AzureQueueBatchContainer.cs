
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    [Serializable]
    internal class AzureQueueBatchContainer : IBatchContainer
    {
        [JsonProperty]
        private EventSequenceToken sequenceToken;

        [JsonProperty]
        private readonly List<object> events;

        [JsonProperty]
        private readonly Dictionary<string, object> requestContext;

        public Guid StreamGuid { get; private set; }

        public String StreamNamespace { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        internal EventSequenceToken RealSequenceToken
        {
            set { sequenceToken = value; }
        }

        [JsonConstructor]
        public AzureQueueBatchContainer(
            Guid streamGuid,
            String streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceToken sequenceToken)
            : this(streamGuid, streamNamespace, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        public AzureQueueBatchContainer(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException("events", "Message contains no events");

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
            return string.Format("[AzureQueueBatchContainer:Stream={0},#Items={1}]", StreamGuid, events.Count);
            //return string.Format("[AzureBatch:#Items={0},Items{1}]", events.Count, Utils.EnumerableToString(events.Select((e, i) => String.Format("{0}-{1}", e, sequenceToken.CreateSequenceTokenForEvent(i)))));
        }
    }
}
