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
    public class SQSBatchContainer : IBatchContainer
    {
        [JsonProperty]
        [Orleans.Id(0)]
        private StreamSequenceToken sequenceToken;

        [JsonProperty]
        [Orleans.Id(1)]
        private readonly List<object> events;

        [JsonProperty]
        [Orleans.Id(2)]
        private readonly Dictionary<string, object> requestContext;

        [NonSerialized]
        // Need to store reference to the original SQS Message to be able to delete it later on.
        // Don't need to serialize it, since we are never interested in sending it to stream consumers.
        public SQSMessage Message;

        [Orleans.Id(3)]
        public StreamId StreamId { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        [JsonConstructor]
        public SQSBatchContainer(
            StreamId streamId,
            List<object> events,
            Dictionary<string, object> requestContext,
            StreamSequenceToken sequenceToken)
            : this(streamId, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        public SQSBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException(nameof(events), "Message contains no events");

            StreamId = streamId;
            this.events = events;
            this.requestContext = requestContext;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            static StreamSequenceToken CreateStreamSequenceToken(StreamSequenceToken tok, int eventIndex)
            {
                return tok switch
                {
                    EventSequenceTokenV2 v2Tok => v2Tok.CreateSequenceTokenForEvent(eventIndex),
                    SQSFIFOSequenceToken fifoTok => fifoTok.CreateSequenceTokenForEvent(eventIndex),
                    _ => throw new NotSupportedException("Unknown SequenceToken provided.")
                };
            }

            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, CreateStreamSequenceToken(sequenceToken, i)));
        }

        internal static SQSMessage ToSQSMessage<T>(
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
            return new SQSMessage
            {
                Body = payload.ToString()
            };
        }

        internal static SQSBatchContainer FromSQSMessage(Serializer<SQSBatchContainer> serializer, SQSMessage msg, long sequenceNumber)
        {
            var json = JObject.Parse(msg.Body);
            var sqsBatch = serializer.Deserialize(json["payload"].ToObject<byte[]>());
            sqsBatch.Message = msg;

            if(msg.Attributes.TryGetValue("SequenceNumber", out var fifoSeqNum))
                sqsBatch.sequenceToken = new SQSFIFOSequenceToken(UInt128.Parse(fifoSeqNum));
            else 
                sqsBatch.sequenceToken = new EventSequenceTokenV2(sequenceNumber);

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
