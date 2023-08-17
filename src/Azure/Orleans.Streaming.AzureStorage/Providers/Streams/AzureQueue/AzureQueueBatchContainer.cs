using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    [Serializable]
    [GenerateSerializer]
    internal class AzureQueueBatchContainer : IBatchContainer
    {
        [JsonProperty]
        [Id(0)]
        private EventSequenceToken sequenceToken;

        [JsonProperty]
        [Id(1)]
        private readonly List<object> events;

        [JsonProperty]
        [Id(2)]
        private readonly Dictionary<string, object> requestContext;

        [Id(3)]
        public StreamId StreamId { get; private set; }

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
            StreamId streamId,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceToken sequenceToken)
            : this(streamId, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        public AzureQueueBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException(nameof(events), "Message contains no events");

            StreamId = streamId;
            this.events = events;
            this.requestContext = requestContext;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, sequenceToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ImportRequestContext()
        {
            if (requestContext != null)
            {
                RequestContextExtensions.Import(requestContext);
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            return string.Format("[AzureQueueBatchContainer:Stream={0},#Items={1}]", StreamId, events.Count);
            //return string.Format("[AzureBatch:#Items={0},Items{1}]", events.Count, Utils.EnumerableToString(events.Select((e, i) => String.Format("{0}-{1}", e, sequenceToken.CreateSequenceTokenForEvent(i)))));
        }
    }
}
