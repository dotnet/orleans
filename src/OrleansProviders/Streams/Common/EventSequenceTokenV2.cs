using System;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Stream sequcen token that tracks sequence nubmer and event index
    /// </summary>
    [RegisterSerializer]
    public class EventSequenceTokenV2 : EventSequenceToken
    {
        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        public EventSequenceTokenV2(long seqNumber) : base(seqNumber)
        {
        }

        /// <summary>
        /// Sequence token constructor
        /// </summary>
        /// <param name="seqNumber"></param>
        /// <param name="eventInd"></param>
        public EventSequenceTokenV2(long seqNumber, int eventInd) : base(seqNumber, eventInd)
        {
        }

        /// <summary>
        /// Register the serializers
        /// </summary>
        public static void Register()
        {
            SerializationManager.Register(typeof(EventSequenceTokenV2), DeepCopy, Serialize, Deserialize);
        }

        /// <summary>
        /// Create a deep copy of the token.
        /// </summary>
        /// <param name="original">The token to copy</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>A copy</returns>
        public static object DeepCopy(object original, ICopyContext context)
        {
            var source = original as EventSequenceTokenV2;
            if (source == null)
            {
                return null;
            }

            var copy = new EventSequenceTokenV2(source.SequenceNumber, source.EventIndex);
            context.RecordCopy(original, copy);
            return copy;
        }

        /// <summary>
        /// Serialize the event sequence token.
        /// </summary>
        /// <param name="untypedInput">The object to serialize.</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expected">The expected type.</param>
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            var writer = context.StreamWriter;
            var typed = untypedInput as EventSequenceTokenV2;
            if (typed == null)
            {
                writer.WriteNull();
                return;
            }

            writer.Write(typed.SequenceNumber);
            writer.Write(typed.EventIndex);
        }

        /// <summary>
        /// Deserializes an event sequence token
        /// </summary>
        /// <param name="expected">The expected type.</param>
        /// <param name="context">The deserialization context.</param>
        /// <returns></returns>
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var result = new EventSequenceTokenV2(reader.ReadLong(), reader.ReadInt());
            context.RecordObject(result);
            return result;
        }
    }
}
