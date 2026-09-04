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
        private readonly byte[] _rawRecord = null!;

        // Payload is local cache of deserialized payloadBytes.  Should never be serialized as part of batch container.  During batch container serialization raw payloadBytes will always be used.
        [NonSerialized]
        private Body? _payload;

        [NonSerialized]
        private StreamId _streamId;

        [NonSerialized]
        private bool _hasStreamId;

        [JsonIgnore]
        [field: NonSerialized]
        internal Serializer<KinesisBatchContainer.Body> Serializer { get; set; } = null!;

        [JsonProperty]
        [Id(1)]
        internal KinesisSequenceToken Token { get; } = null!;

        private KinesisBatchContainer(Record record, Serializer<KinesisBatchContainer.Body> serializer, long sequenceId)
            : this(record, serializer, streamName: null, shardId: null, sequenceId)
        {
        }

        private KinesisBatchContainer(
            Record record,
            Serializer<KinesisBatchContainer.Body> serializer,
            string? streamName,
            string? shardId,
            long sequenceId)
        {
            this.Serializer = serializer;
            this._rawRecord = record.Data.ToArray();

            Token = new KinesisSequenceToken(streamName, shardId, record.SequenceNumber, sequenceId, 0);
        }

        private KinesisBatchContainer(
            byte[] rawRecord,
            Serializer<KinesisBatchContainer.Body> serializer,
            StreamId streamId,
            string streamName,
            string shardId,
            string shardSequence,
            long sequenceId)
        {
            Serializer = serializer;
            _rawRecord = rawRecord;
            _streamId = streamId;
            _hasStreamId = true;
            Token = new KinesisSequenceToken(streamName, shardId, shardSequence, sequenceId, 0);
        }

        [GeneratedActivatorConstructor]
        internal KinesisBatchContainer(Serializer<KinesisBatchContainer.Body> serializer)
        {
            this.Serializer = serializer;
        }

        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        public StreamId StreamId => _hasStreamId ? _streamId : GetPayload().StreamId;

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        public StreamSequenceToken SequenceToken => Token;

        private Body GetPayload() => _payload ??= this.Serializer.Deserialize(_rawRecord)!;

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return GetPayload().Events
                .Select((item, index) => (item, index))
                .Where(static entry => entry.item is T)
                .Select(entry => Tuple.Create<T, StreamSequenceToken>(
                    (T)entry.item,
                    Token.CreateSequenceTokenForEvent(entry.index)));
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

        public int CompareTo(KinesisBatchContainer? other)
            => other is null ? 1 : Token.CompareTo(other.Token);

        [Serializable]
        [GenerateSerializer]
        internal class Body
        {
            [Id(0)]
            public List<object> Events { get; set; } = null!;

            [Id(1)]
            public Dictionary<string, object>? RequestContext { get; set; }

            [Id(2)]
            public StreamId StreamId { get; set; }
        }

        internal static byte[] ToKinesisPayload<T>(Serializer<KinesisBatchContainer.Body> serializer, StreamId streamId, IEnumerable<T> events, Dictionary<string, object>? requestContext)
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

        internal static KinesisBatchContainer FromKinesisRecord(
            Serializer<KinesisBatchContainer.Body> serializer,
            Record record,
            string streamName,
            string shardId,
            long sequenceId)
            => new(record, serializer, streamName, shardId, sequenceId);

        internal static KinesisBatchContainer FromCachedRecord(
            Serializer<KinesisBatchContainer.Body> serializer,
            StreamId streamId,
            byte[] rawRecord,
            string streamName,
            string shardId,
            string shardSequence,
            long sequenceId)
            => new(rawRecord, serializer, streamId, streamName, shardId, shardSequence, sequenceId);
    }
}
