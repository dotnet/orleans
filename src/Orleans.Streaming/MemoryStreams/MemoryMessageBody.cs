
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.Providers
{
    /// <summary>
    /// Implementations of this interface are responsible for serializing MemoryMessageBody objects
    /// </summary>
    public interface IMemoryMessageBodySerializer
    {
        /// <summary>
        /// Serialize <see cref="MemoryMessageBody"/> to an array segment of bytes.
        /// </summary>
        /// <param name="body">The body.</param>
        /// <returns>The serialized data.</returns>
        ArraySegment<byte> Serialize(MemoryMessageBody body);

        /// <summary>
        /// Deserialize an array segment into a <see cref="MemoryMessageBody"/>
        /// </summary>
        /// <param name="bodyBytes">The body bytes.</param>
        /// <returns>The deserialized message body.</returns>
        MemoryMessageBody Deserialize(ArraySegment<byte> bodyBytes);
    }

    /// <summary>
    /// Default <see cref="IMemoryMessageBodySerializer"/> implementation.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    [SerializationCallbacks(typeof(Runtime.OnDeserializedCallbacks))]
    public sealed class DefaultMemoryMessageBodySerializer : IMemoryMessageBodySerializer, IOnDeserialized
    {
        [NonSerialized]
        private Serializer<MemoryMessageBody> serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultMemoryMessageBodySerializer" /> class.
        /// </summary>
        /// <param name="serializer">The serializer.</param>
        public DefaultMemoryMessageBodySerializer(Serializer<MemoryMessageBody> serializer)
        {
            this.serializer = serializer;
        }

        /// <inheritdoc />
        public ArraySegment<byte> Serialize(MemoryMessageBody body)
        {
            return new ArraySegment<byte>(serializer.SerializeToArray(body));
        }

        /// <inheritdoc />
        public MemoryMessageBody Deserialize(ArraySegment<byte> bodyBytes)
        {
            return serializer.Deserialize(bodyBytes.ToArray());
        }

        /// <inheritdoc />
        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            this.serializer = context.ServiceProvider.GetRequiredService<Serializer<MemoryMessageBody>>();
        }
    }

    /// <summary>
    /// Message body used by the in-memory stream provider.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class MemoryMessageBody
    {        
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryMessageBody"/> class.
        /// </summary>
        /// <param name="events">Events that are part of this message.</param>
        /// <param name="requestContext">Context in which this message was sent.</param>        
        public MemoryMessageBody(IEnumerable<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            Events = events.ToList();
            RequestContext = requestContext;
        }

        /// <summary>
        /// Gets the events in the message.
        /// </summary>
        [Id(0)]
        public List<object> Events { get; }

        /// <summary>
        /// Gets the message request context.
        /// </summary>
        [Id(1)]
        public Dictionary<string, object> RequestContext { get; }
    }
}
