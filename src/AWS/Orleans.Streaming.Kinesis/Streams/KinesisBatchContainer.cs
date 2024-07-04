using Amazon.Kinesis.Model;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streaming.Kinesis
{
    [Serializable]
    [Orleans.GenerateSerializer]
    internal class KinesisBatchContainer : IBatchContainer, IComparable<KinesisBatchContainer>
    {
        [JsonProperty]
        [Id(0)]
        private readonly byte[] _rawRecord;

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized]
        private Body _payload;

        [JsonIgnore]
        [field: NonSerialized]
        internal Serializer<KinesisBatchContainer.Body> Serializer { get; set; }

        [JsonProperty]
        [Id(1)]
        internal KinesisSequenceToken Token { get; }

        private KinesisBatchContainer(Record record, Serializer<KinesisBatchContainer.Body> serializer, long sequenceId)
        {
            this.Serializer = serializer;
            this._rawRecord = record.Data.ToArray();

            Token = new KinesisSequenceToken(record.SequenceNumber, sequenceId, 0);
        }

        [GeneratedActivatorConstructor]
        internal KinesisBatchContainer(Serializer<KinesisBatchContainer.Body> serializer)
        {
            this.Serializer = serializer;
        }

        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        public StreamId StreamId => GetPayload().StreamId;

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        public StreamSequenceToken SequenceToken => Token;

        private Body GetPayload() => _payload ?? (_payload = this.Serializer.Deserialize(_rawRecord));

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return GetPayload().Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, new KinesisSequenceToken(Token.ShardSequence, Token.SequenceNumber, i)));
        }

        /// <summary>
        /// Gives an opportunity to IBatchContainer to set any data in the RequestContext before this IBatchContainer is sent to consumers.
        /// It can be the data that was set at the time event was generated and enqueued into the persistent provider or any other data.
        /// </summary>
        /// <returns>True if the RequestContext was indeed modified, false otherwise.</returns>
        public bool ImportRequestContext()
        {
            if (GetPayload().RequestContext != null)
            {
                RequestContextExtensions.Import(GetPayload().RequestContext);
                return true;
            }
            return false;
        }

        public int CompareTo(KinesisBatchContainer other)
            => Token.SequenceNumber.CompareTo(other.SequenceToken.SequenceNumber);

        [Serializable]
        [GenerateSerializer]
        internal class Body
        {
            [Id(0)]
            public List<object> Events { get; set; }

            [Id(1)]
            public Dictionary<string, object> RequestContext { get; set; }

            [Id(2)]
            public StreamId StreamId { get; set; }
        }

        internal static byte[] ToKinesisPayload<T>(Serializer<KinesisBatchContainer.Body> serializer, StreamId streamId, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var payload = new Body
            {
                Events = events.Cast<object>().ToList(),
                RequestContext = requestContext,
                StreamId = streamId,
            };

            return serializer.SerializeToArray(payload);
        }

        internal static KinesisBatchContainer FromKinesisRecord(Serializer<KinesisBatchContainer.Body> serializer, Record record, long sequenceId)
        {
            return new KinesisBatchContainer(record, serializer, sequenceId);
        }
    }
}
