using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;

namespace Orleans.Providers.Streams.AzureQueue
{
    [RegisterSerializer]
    internal class AzureQueueBatchContainerV2 : AzureQueueBatchContainer
    {
        [JsonConstructor]
        internal AzureQueueBatchContainerV2(
            Guid streamGuid, 
            string streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext, 
            EventSequenceTokenV2 sequenceToken)
            : base(streamGuid, streamNamespace, events, requestContext, sequenceToken)
        {
        }

        internal AzureQueueBatchContainerV2(
            Guid streamGuid,
            string streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext)
            : base(streamGuid, streamNamespace, events, requestContext)
        {
        }

        private AzureQueueBatchContainerV2() : base()
        {
        }

        /// <summary>
        /// Creates a deep copy of an object
        /// </summary>
        /// <param name="original">The object to create a copy of</param>
        /// <returns>The copy.</returns>
        public static object DeepCopy(object original)
        {
            var source = original as AzureQueueBatchContainerV2;
            if (source == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            var copy = new AzureQueueBatchContainerV2();
            SerializationContext.Current.RecordObject(original, copy);
            var token = source.sequenceToken == null ? null : new EventSequenceTokenV2(source.sequenceToken.SequenceNumber, source.sequenceToken.EventIndex);
            var events = source.events?.Select(SerializationManager.DeepCopyInner).ToList();
            var context = source.requestContext?.ToDictionary(kv => kv.Key, kv => SerializationManager.DeepCopyInner(kv.Value));
            copy.SetValues(source.StreamGuid, source.StreamNamespace, events, context, token);
            return copy;
        }

        /// <summary>
        /// Serializes the container to the binary stream.
        /// </summary>
        /// <param name="untypedInput">The object to serialize</param>
        /// <param name="writer">The stream to write to</param>
        /// <param name="expected">The expected type</param>
        public static void Serialize(object untypedInput, BinaryTokenStreamWriter writer, Type expected)
        {
            var typed = untypedInput as AzureQueueBatchContainerV2;
            if (typed == null)
            {
                throw new SerializationException();
            }

            writer.Write(typed.StreamGuid);
            writer.Write(typed.StreamNamespace);
            WriteOrSerializeInner(typed.sequenceToken, writer);
            WriteOrSerializeInner(typed.events, writer);
            WriteOrSerializeInner(typed.requestContext, writer);
        }

        /// <summary>
        /// Deserializes the container from the data stream.
        /// </summary>
        /// <param name="expected">The expected type</param>
        /// <param name="reader">The stream reader</param>
        /// <returns>The deserialized value</returns>
        public static object Deserialize(Type expected, BinaryTokenStreamReader reader)
        {
            var deserialized = new AzureQueueBatchContainerV2();
            DeserializationContext.Current.RecordObject(deserialized);
            var guid = reader.ReadGuid();
            var ns = reader.ReadString();
            var eventToken = SerializationManager.DeserializeInner<EventSequenceTokenV2>(reader);
            var events = SerializationManager.DeserializeInner<List<object>>(reader);
            var context = SerializationManager.DeserializeInner<Dictionary<string, object>>(reader);
            deserialized.SetValues(guid, ns, events, context, eventToken);
            return deserialized;
        }

        /// <summary>
        /// Register the serializer methods.
        /// </summary>
        public static void Register()
        {
            SerializationManager.Register(typeof(AzureQueueBatchContainerV2), DeepCopy, Serialize, Deserialize);
        }

        private static void WriteOrSerializeInner<T>(T val, BinaryTokenStreamWriter writer) where T : class
        {
            if (val == null)
            {
                writer.WriteNull();
            }
            else
            {
                SerializationManager.SerializeInner(val, writer, val.GetType());
            }
        }

        private void SetValues(Guid streamGuid, string streamNamespace, List<object> events, Dictionary<string, object> requestContext, EventSequenceTokenV2 sequenceToken)
        {
            this.StreamGuid = streamGuid;
            this.StreamNamespace = streamNamespace;
            this.events = events;
            this.requestContext = requestContext;
            this.sequenceToken = sequenceToken;
        }
    }
}
