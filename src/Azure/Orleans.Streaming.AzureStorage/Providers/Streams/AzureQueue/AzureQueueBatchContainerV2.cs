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
    [GenerateSerializer]
    internal class AzureQueueBatchContainerV2 : IBatchContainer
    {
        [JsonProperty]
        [Id(0)]
        private EventSequenceTokenV2 sequenceToken;

        [JsonProperty]
        [Id(1)]
        private readonly List<object> events;

        [JsonProperty]
        [Id(2)]
        private readonly Dictionary<string, object> requestContext;

        [Id(3)]
        public StreamId StreamId { get; }

        public StreamSequenceToken SequenceToken => sequenceToken;

        internal EventSequenceTokenV2 RealSequenceToken
        {
            set { sequenceToken = value; }
        }

        [JsonConstructor]
        public AzureQueueBatchContainerV2(
            StreamId streamId,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceTokenV2 sequenceToken)
            : this(streamId, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        public AzureQueueBatchContainerV2(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
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
            return $"[AzureQueueBatchContainerV2:Stream={StreamId},#Items={events.Count}]";
        }
    }
}
