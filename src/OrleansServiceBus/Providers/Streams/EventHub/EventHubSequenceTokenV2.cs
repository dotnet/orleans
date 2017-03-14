using System;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Event Hub messages consist of a batch of application layer events, so EventHub tokens contain three pieces of information.
    /// EventHubOffset - this is a unique value per partition that is used to start reading from this message in the partition.
    /// SequenceNumber - EventHub sequence numbers are unique ordered message IDs for messages within a partition.  
    ///   The SequenceNumber is required for uniqueness and ordering of EventHub messages within a partition.
    /// event Index - Since each EventHub message may contain more than one application layer event, this value
    ///   indicates which application layer event this token is for, within an EventHub message.  It is required for uniqueness
    ///   and ordering of application layer events within an EventHub message.
    /// </summary>
    public class EventHubSequenceTokenV2 : EventHubSequenceToken
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="eventHubOffset">EventHub offset within the partition from which this message came.</param>
        /// <param name="sequenceNumber">EventHub sequenceNumber for this message.</param>
        /// <param name="eventIndex">Index into a batch of events, if multiple events were delivered within a single EventHub message.</param>
        public EventHubSequenceTokenV2(string eventHubOffset, long sequenceNumber, int eventIndex)
            : base(eventHubOffset, sequenceNumber, eventIndex)
        {
        }

        /// <summary>
        /// Create a deep copy of the token.
        /// </summary>
        /// <param name="original">The token to copy</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>A copy</returns>
        [CopierMethod]
        public static object DeepCopy(object original, ICopyContext context)
        {
            var source = original as EventHubSequenceTokenV2;
            if (source == null)
            {
                return null;
            }

            var copy = new EventHubSequenceTokenV2(source.EventHubOffset, source.SequenceNumber, source.EventIndex);
            context.RecordCopy(original, copy);
            return copy;
        }

        /// <summary>
        /// Serialize the event sequence token.
        /// </summary>
        /// <param name="untypedInput">The object to serialize.</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expected">The expected type.</param>
        [SerializerMethod]
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            var typed = untypedInput as EventHubSequenceTokenV2;
            var writer = context.StreamWriter;
            if (typed == null)
            {
                writer.WriteNull();
                return;
            }

            writer.Write(typed.EventHubOffset);
            writer.Write(typed.SequenceNumber);
            writer.Write(typed.EventIndex);
        }

        /// <summary>
        /// Deserializes an event sequence token
        /// </summary>
        /// <param name="expected">The expected type.</param>
        /// <param name="context">The deserialization context.</param>
        /// <returns></returns>
        [DeserializerMethod]
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var deserialized = new EventHubSequenceTokenV2(reader.ReadString(), reader.ReadLong(), reader.ReadInt());
            context.RecordObject(deserialized);
            return deserialized;
        }
    }
}
