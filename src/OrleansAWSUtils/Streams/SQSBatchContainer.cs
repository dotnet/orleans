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
using System.Text;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    [Serializable]
    internal class SQSBatchContainer : IBatchContainer
    {
        [JsonProperty]
        private EventSequenceTokenV2 sequenceToken;

        [JsonProperty]
        private readonly List<object> events;

        [JsonProperty]
        private readonly Dictionary<string, object> requestContext;

        [NonSerialized]
        // Need to store reference to the original SQS Message to be able to delete it later on.
        // Don't need to serialize it, since we are never interested in sending it to stream consumers.
        internal SQSMessage Message;

        public Guid StreamGuid { get; private set; }

        public string StreamNamespace { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        [JsonConstructor]
        private SQSBatchContainer(
            Guid streamGuid,
            String streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext,
            EventSequenceTokenV2 sequenceToken)
            : this(streamGuid, streamNamespace, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        private SQSBatchContainer(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
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

        internal static SendMessageRequest ToSQSMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var sqsBatchMessage = new SQSBatchContainer(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = SerializationManager.SerializeToByteArray(sqsBatchMessage);
            var payload = new JObject();
            payload.Add("payload", JToken.FromObject(rawBytes));
            return new SendMessageRequest { MessageBody = payload.ToString() };
        }

        internal static SQSBatchContainer FromSQSMessage(SQSMessage msg, long sequenceId)
        {
            var json = JObject.Parse(msg.Body);
            var sqsBatch = SerializationManager.DeserializeFromByteArray<SQSBatchContainer>(json["payload"].ToObject<byte[]>());
            sqsBatch.Message = msg;
            sqsBatch.sequenceToken = new EventSequenceTokenV2(sequenceId);
            return sqsBatch;
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
            return string.Format("[SQSBatchContainer:Stream={0},#Items={1}]", StreamGuid, events.Count);
        }
    }
}
