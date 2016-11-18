using System;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;

namespace OrleansServiceBus.Providers.Streams.EventHub
{
    /// <summary>
    /// Event Hub messages consist of a batch of application layer events, so EventHub tokens contain three pieces of information.
    /// EventHubOffset - this is a unique value per partition that is used to start reading from this message in the partition.
    /// SequenceNumber - EventHub sequence numbers are unique ordered message IDs for messages within a partition.  
    ///   The SequenceNumber is required for uniqueness and ordering of EventHub messages within a partition.
    /// event Index - Since each EventHub message may contain more than one application layer event, this value
    ///   indicates which application layer event this token is for, within an EventHub message.  It is required for uniqueness
    ///   and ordering of aplication layer events within an EventHub message.
    /// </summary>
    [RegisterSerializer]
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
        /// Register the serializers
        /// </summary>
        public static void Register()
        {
            SerializationManager.Register(typeof(EventHubSequenceTokenV2), DeepCopy, Serialize, Deserialize);
        }

        /// <summary>
        /// Create a deep copy of the token.
        /// </summary>
        /// <param name="original">The token to copy</param>
        /// <returns>A copy</returns>
        public static object DeepCopy(object original)
        {
            var source = original as EventHubSequenceTokenV2;
            if (source == null)
            {
                return null;
            }

            var copy = new EventHubSequenceTokenV2(source.EventHubOffset, source.SequenceNumber, source.EventIndex);
            SerializationContext.Current.RecordObject(original, copy);
            return copy;
        }

        /// <summary>
        /// Serialize the event sequence token.
        /// </summary>
        /// <param name="untypedInput">The object to serialize.</param>
        /// <param name="writer">The writer to write the binary stream to.</param>
        /// <param name="expected">The expected type.</param>
        public static void Serialize(object untypedInput, BinaryTokenStreamWriter writer, Type expected)
        {
            var typed = untypedInput as EventHubSequenceTokenV2;
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
        /// <param name="reader">The binary stream to read from.</param>
        /// <returns></returns>
        public static object Deserialize(Type expected, BinaryTokenStreamReader reader)
        {
            var deserialized = new EventHubSequenceTokenV2(reader.ReadString(), reader.ReadLong(), reader.ReadInt());
            DeserializationContext.Current.RecordObject(deserialized);
            return deserialized;
        }
    }
}
