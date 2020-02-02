using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Storage.Queue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Original data adapter.  Here to maintain backwards compatibility, but does not support json and other custom serializers
    /// </summary>
    public class AzureQueueDataAdapterV1 : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>, IOnDeserialized
    {
        private SerializationManager serializationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueDataAdapterV1"/> class.
        /// </summary>
        /// <param name="serializationManager"></param>
        public AzureQueueDataAdapterV1(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public CloudQueueMessage ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainer(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);

            //new CloudQueueMessage(byte[]) not supported in netstandard, taking a detour to set it
            var cloudQueueMessage = new CloudQueueMessage(null as string);
            cloudQueueMessage.SetMessageContent2(rawBytes);
            return cloudQueueMessage;
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainer>(cloudMsg.AsBytes);
            azureQueueBatch.RealSequenceToken = new EventSequenceToken(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.GetSerializationManager();
        }
    }

    /// <summary>
    /// Data adapter that uses types that support custom serializers (like json).
    /// </summary>
    public class AzureQueueDataAdapterV2 : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>, IOnDeserialized
    {
        private SerializationManager serializationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueDataAdapterV2"/> class.
        /// </summary>
        /// <param name="serializationManager"></param>
        public AzureQueueDataAdapterV2(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public CloudQueueMessage ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);

            //new CloudQueueMessage(byte[]) not supported in netstandard, taking a detour to set it
            var cloudQueueMessage = new CloudQueueMessage(null as string);
            cloudQueueMessage.SetMessageContent2(rawBytes);
            return cloudQueueMessage;
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(cloudMsg.AsBytes);
            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.GetSerializationManager();
        }
    }
}
