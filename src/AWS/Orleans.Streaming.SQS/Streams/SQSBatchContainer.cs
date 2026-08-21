using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Amazon.SQS;
using Amazon.SQS.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    [Serializable]
    [Orleans.GenerateSerializer]
    internal class SQSBatchContainer : IBatchContainer
    {
        [JsonProperty]
        [Orleans.Id(0)]
        private StreamSequenceToken sequenceToken = null!;

        [JsonProperty]
        [Orleans.Id(1)]
        private readonly List<object> events;

        [JsonProperty]
        [Orleans.Id(2)]
        private readonly Dictionary<string, object>? requestContext;

        [Orleans.Id(3)]
        public StreamId StreamId { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        [JsonConstructor]
        internal SQSBatchContainer(
            StreamId streamId,
            List<object> events,
            Dictionary<string, object>? requestContext,
            StreamSequenceToken sequenceToken)
            : this(streamId, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        private SQSBatchContainer(StreamId streamId, List<object> events, Dictionary<string, object>? requestContext)
        {
            if (events == null) throw new ArgumentNullException(nameof(events), "Message contains no events");

            StreamId = streamId;
            this.events = events;
            this.requestContext = requestContext;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            static StreamSequenceToken CreateStreamSequenceToken(StreamSequenceToken token, int eventIndex)
            {
                return token switch
                {
                    EventSequenceTokenV2 v2Tok => v2Tok.CreateSequenceTokenForEvent(eventIndex),
                    SQSFIFOSequenceToken fifoTok => fifoTok.CreateSequenceTokenForEvent(eventIndex),
                    _ => throw new NotSupportedException($"Unsupported sequence token type: {token.GetType().FullName}.")
                };
            }

            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, CreateStreamSequenceToken(sequenceToken, i)));
        }

        internal static SQSMessage ToSQSMessage<T>(
            Serializer<SQSBatchContainer> serializer,
            StreamId streamId,
            IEnumerable<T> events,
            Dictionary<string, object>? requestContext)
        {
            var sqsBatchMessage = new SQSBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
            var rawBytes = serializer.SerializeToArray(sqsBatchMessage);
            var payload = new JObject
            {
                { "payload", JToken.FromObject(rawBytes) }
            };
            return new SQSMessage
            {
                Body = payload.ToString(),
            };
        }

        internal static SQSBatchContainer FromSQSMessage(Serializer<SQSBatchContainer> serializer, SQSMessage msg, long sequenceNumber)
        {
            var json = JObject.Parse(msg.Body);
            // A valid SQS stream message contains a serialized batch payload.
            var sqsBatch = serializer.Deserialize(json["payload"]!.ToObject<byte[]>()!)!;
            if (msg.Attributes is { } attributes
                && attributes.TryGetValue(MessageSystemAttributeName.SequenceNumber, out var fifoSeqNum))
            {
                sqsBatch.sequenceToken = new SQSFIFOSequenceToken(
                    sqsBatch.StreamId,
                    UInt128.Parse(fifoSeqNum, CultureInfo.InvariantCulture),
                    sequenceNumber);
            }
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
