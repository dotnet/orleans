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
        /// <param name="context">The copy context.</param>
        /// <returns>The copy.</returns>
        public static object DeepCopy(object original, ICopyContext context)
        {
            var source = original as AzureQueueBatchContainerV2;
            if (source == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            var copy = new AzureQueueBatchContainerV2();
            context.RecordCopy(original, copy);
            var token = source.sequenceToken == null ? null : new EventSequenceTokenV2(source.sequenceToken.SequenceNumber, source.sequenceToken.EventIndex);
            List<object> events = null;
            if (source.events != null)
            {
                events = new List<object>(source.events.Count);
                foreach (var item in source.events)
                {
                    events.Add(SerializationManager.DeepCopyInner(item, context));
                }
            }
            
            var ctx = source.requestContext?.ToDictionary(kv => kv.Key, kv => SerializationManager.DeepCopyInner(kv.Value, context));
            copy.SetValues(source.StreamGuid, source.StreamNamespace, events, ctx, token);
            return copy;
        }

        /// <summary>
        /// Serializes the container to the binary stream.
        /// </summary>
        /// <param name="untypedInput">The object to serialize</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expected">The expected type</param>
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            var typed = untypedInput as AzureQueueBatchContainerV2;
            if (typed == null)
            {
                throw new SerializationException();
            }

            context.StreamWriter.Write(typed.StreamGuid);
            context.StreamWriter.Write(typed.StreamNamespace);
            WriteOrSerializeInner(typed.sequenceToken, context);
            WriteOrSerializeInner(typed.events, context);
            WriteOrSerializeInner(typed.requestContext, context);
        }

        /// <summary>
        /// Deserializes the container from the data stream.
        /// </summary>
        /// <param name="expected">The expected type</param>
        /// <param name="context">The deserialization context.</param>
        /// <returns>The deserialized value</returns>
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var deserialized = new AzureQueueBatchContainerV2();
            context.RecordObject(deserialized);
            var guid = reader.ReadGuid();
            var ns = reader.ReadString();
            var eventToken = SerializationManager.DeserializeInner<EventSequenceTokenV2>(context);
            var events = SerializationManager.DeserializeInner<List<object>>(context);
            var ctx = SerializationManager.DeserializeInner<Dictionary<string, object>>(context);
            deserialized.SetValues(guid, ns, events, ctx, eventToken);
            return deserialized;
        }

        /// <summary>
        /// Register the serializer methods.
        /// </summary>
        public static void Register()
        {
            SerializationManager.Register(typeof(AzureQueueBatchContainerV2), DeepCopy, Serialize, Deserialize);
        }

        private static void WriteOrSerializeInner<T>(T val, ISerializationContext context) where T : class
        {
            if (val == null)
            {
                context.StreamWriter.WriteNull();
            }
            else
            {
                SerializationManager.SerializeInner(val, context, val.GetType());
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
