
using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.Providers
{
    /// <summary>
    /// Implementations of this interface are responsible for serializing MemoryMessageBody objects
    /// </summary>
    public interface IMemoryMessageBodySerializer
    {
        /// <summary>
        /// Serialize MemoryMessageBody to an array segment of bytes.
        /// </summary>
        /// <param name="body"></param>
        /// <returns></returns>
        ArraySegment<byte> Serialize(MemoryMessageBody body);

        /// <summary>
        /// Deserialize an array segment into a MemoryMessageBody
        /// </summary>
        /// <param name="bodyBytes"></param>
        /// <returns></returns>
        MemoryMessageBody Deserialize(ArraySegment<byte> bodyBytes);
    }

    /// <summary>
    /// Default IMemoryMessageBodySerializer
    /// </summary>
    [Serializable]
    public class DefaultMemoryMessageBodySerializer : IMemoryMessageBodySerializer, IOnDeserialized
    {
        [NonSerialized]
        private SerializationManager serializationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultMemoryMessageBodySerializer"/> class.
        /// </summary>
        /// <param name="serializationManager"></param>
        public DefaultMemoryMessageBodySerializer(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <inheritdoc />
        public ArraySegment<byte> Serialize(MemoryMessageBody body)
        {
            return new ArraySegment<byte>(serializationManager.SerializeToByteArray(body));
        }

        /// <inheritdoc />
        public MemoryMessageBody Deserialize(ArraySegment<byte> bodyBytes)
        {
            return serializationManager.DeserializeFromByteArray<MemoryMessageBody>(bodyBytes.ToArray());
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.GetSerializationManager();
        }
    }

    /// <summary>
    /// Body of message sent over MemoryStreamProvider
    /// </summary>
    [Serializable]
    public class MemoryMessageBody
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="events">Events that are part of this message</param>
        /// <param name="contex">Context in which this message was sent</param>
        public MemoryMessageBody(IEnumerable<object> events, Dictionary<string, object> contex)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            Events = events.ToList();
            RequestContext = contex;
        }

        /// <summary>
        /// Events in message
        /// </summary>
        public List<object> Events { get; }

        /// <summary>
        /// Message context
        /// </summary>
        public Dictionary<string, object> RequestContext { get; }
    }
}
