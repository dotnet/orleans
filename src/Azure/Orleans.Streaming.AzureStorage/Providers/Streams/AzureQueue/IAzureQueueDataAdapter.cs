using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Original data adapter.  Here to maintain backwards compatibility, but does not support json and other custom serializers
    /// </summary>
    [SerializationCallbacks(typeof(OnDeserializedCallbacks))]
    public class AzureQueueDataAdapterV1 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
    {
        private Serializer<AzureQueueBatchContainer> serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueDataAdapterV1"/> class.
        /// </summary>
        /// <param name="serializer"></param>
        public AzureQueueDataAdapterV1(Serializer serializer)
        {
            this.serializer = serializer.GetSerializer<AzureQueueBatchContainer>();
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializer.SerializeToArray(azureQueueBatchMessage);
            return Convert.ToBase64String(rawBytes);
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializer.Deserialize(Convert.FromBase64String(cloudMsg));
            azureQueueBatch.RealSequenceToken = new EventSequenceToken(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            this.serializer = context.ServiceProvider.GetRequiredService<Serializer<AzureQueueBatchContainer>>();
        }
    }

    /// <summary>
    /// Data adapter that uses types that support custom serializers (like json).
    /// </summary>
    [SerializationCallbacks(typeof(OnDeserializedCallbacks))]
    public class AzureQueueDataAdapterV2 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
    {
        private Serializer<AzureQueueBatchContainerV2> serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueDataAdapterV2"/> class.
        /// </summary>
        /// <param name="serializer"></param>
        public AzureQueueDataAdapterV2(Serializer serializer)
        {
            this.serializer = serializer.GetSerializer<AzureQueueBatchContainerV2>();
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamId, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializer.SerializeToArray(azureQueueBatchMessage);
            return Convert.ToBase64String(rawBytes);
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializer.Deserialize(Convert.FromBase64String(cloudMsg));
            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            this.serializer = context.ServiceProvider.GetRequiredService<Serializer<AzureQueueBatchContainerV2>>();
        }
    }
}
