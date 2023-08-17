using Amazon.SQS.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    [Serializable]
    [Orleans.GenerateSerializer]
    internal class SQSBatchContainer : IBatchContainer
    {
        [JsonProperty]
        [Orleans.Id(0)]
        private EventSequenceTokenV2 sequenceToken;

        [JsonProperty]
        [Orleans.Id(1)]
        private readonly List<object> events;

        [JsonProperty]
        [Orleans.Id(2)]
        private readonly Dictionary<string, object> requestContext;

        [NonSerialized]
        // Need to store reference to the original SQS Message to be able to delete it later on.
        // Don't need to serialize it, since we are never interested in sending it to stream consumers.
        internal SQSMessage Message;

        [Orleans.Id(3)]
        public StreamId StreamId { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        [JsonConstructor]
        private SQSBatchContainer(
            StreamId streamId,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceTokenV2 sequenceToken)
            : this(streamId, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        private SQSBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
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

        internal static SendMessageRequest ToSQSMessage<T>(
            Serializer<SQSBatchContainer> serializer,
            StreamId streamId,
            IEnumerable<T> events,
            Dictionary<string, object> requestContext)
        {
            var sqsBatchMessage = new SQSBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
            var rawBytes = serializer.SerializeToArray(sqsBatchMessage);
            var payload = new JObject
            {
                { "payload", JToken.FromObject(rawBytes) }
            };
            return new SendMessageRequest
            {
                MessageBody = payload.ToString()
            };
        }

        internal static SQSBatchContainer FromSQSMessage(Serializer<SQSBatchContainer> serializer, SQSMessage msg, long sequenceId)
        {
            var json = JObject.Parse(msg.Body);
            var sqsBatch = serializer.Deserialize(json["payload"].ToObject<byte[]>());
            sqsBatch.Message = msg;
            sqsBatch.sequenceToken = new EventSequenceTokenV2(sequenceId);
            return sqsBatch;
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
            return string.Format("[SQSBatchContainer:Stream={0},#Items={1}]", StreamId, events.Count);
        }
    }
}
