using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    [Serializable]
    public class RabbitMQBatchContainer : IBatchContainer, IOnDeserialized
    {
        [JsonProperty]
        private readonly RabbitMQMessage _rabbitMQMessage;

        [JsonIgnore]
        [NonSerialized]
        private SerializationManager _serializationManager;

        [JsonProperty]
        private readonly RabbitMQSequenceToken _token;

        [NonSerialized]
        private Body payload;
        private Body GetPayload() => payload ?? (payload = _serializationManager.DeserializeFromByteArray<Body>(_rabbitMQMessage.Message));

        [Serializable]
        private class Body
        {
            public List<object> Events { get; set; }
            public Dictionary<string, object> RequestContext { get; set; }
        }

        /// <summary>
        /// Batch container that delivers events from cached RabbitMQ data associated with an Orleans stream.
        /// </summary>
        /// <param name="rabbitMQMessage"></param>
        /// <param name="serializationManager"></param>
        public RabbitMQBatchContainer(RabbitMQMessage rabbitMQMessage, SerializationManager serializationManager)
        {
            _rabbitMQMessage = rabbitMQMessage;
            _serializationManager = serializationManager;
            _token = new RabbitMQSequenceToken(rabbitMQMessage.SequenceNumber, 0);
        }

        public Guid StreamGuid => _rabbitMQMessage.StreamIdentity.Guid;

        public string StreamNamespace => _rabbitMQMessage.StreamIdentity.Namespace;

        public StreamSequenceToken SequenceToken => _token;

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return GetPayload().Events.Cast<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, new RabbitMQSequenceToken(_token.SequenceNumber, i)));
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

        public void OnDeserialized(ISerializerContext context)
        {
            _serializationManager = context.GetSerializationManager();
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            return true;
        }

        /// <summary>
        /// Generate a net new RabbitMQ message to be delivered to the queue.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="serializationManager"></param>
        /// <param name="streamGuid"></param>
        /// <param name="streamNamespace"></param>
        /// <param name="events"></param>
        /// <param name="requestsContext"></param>
        /// <returns></returns>
        public static RabbitMQMessage ToRabbitMQMessage<T>(SerializationManager serializationManager, Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestsContext)
        {
            var props = new Dictionary<string, object>() {
                    { "x-opt-orleans-stream-namespace", $"{streamNamespace}"},
                    { "x-opt-orleans-stream-guid", $"{streamGuid}" }
                };
            props.Concat(requestsContext.Where(x => !props.ContainsKey(x.Key)));

            // the events parameter is just the raw message payload coming from `OnNextAsync()`.
            var eventPayload = serializationManager.SerializeToByteArray(events);

            var rabbitMessageData = new RabbitMQMessage()
            {
                Message = eventPayload,
                Properties = props,
            };
            return rabbitMessageData;
        }
    }
}
