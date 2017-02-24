using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Converts event data to and from cloud queue message
    /// </summary>
    public interface IAzureQueueDataAdapter
    {
        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        CloudQueueMessage ToCloudQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext);

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        IBatchContainer FromCloudQueueMessage(CloudQueueMessage cloudMsg, long sequenceId);
    }

    /// <summary>
    /// Original data adapter.  Here to maintain backwards compatablity, but does not support json and other custom serializers
    /// </summary>
    public class AzureQueueDataAdapterV1 : IAzureQueueDataAdapter, IOnDeserialized
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
        public CloudQueueMessage ToCloudQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainer(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);

            //new CloudQueueMessage(byte[]) not supported in netstandard, taking a detour to set it
            var cloudQueueMessage = new CloudQueueMessage(null as string);
            cloudQueueMessage.SetMessageContent(rawBytes);
            return cloudQueueMessage;
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromCloudQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainer>(cloudMsg.AsBytes);
            azureQueueBatch.RealSequenceToken = new EventSequenceToken(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.SerializationManager;
        }
    }

    /// <summary>
    /// Data adapter that uses types that support custom serializers (like json).
    /// </summary>
    public class AzureQueueDataAdapterV2 : IAzureQueueDataAdapter, IOnDeserialized
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
        public CloudQueueMessage ToCloudQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);

            //new CloudQueueMessage(byte[]) not supported in netstandard, taking a detour to set it
            var cloudQueueMessage = new CloudQueueMessage(null as string);
            cloudQueueMessage.SetMessageContent(rawBytes);
            return cloudQueueMessage;
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromCloudQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(cloudMsg.AsBytes);
            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.SerializationManager;
        }
    }
}
