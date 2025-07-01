using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.AzureStorage.Migration.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Data adapter that uses types that support custom serializers (like json).
    /// </summary>
    public class AzureQueueDataAdapterMigrationV1 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
    {
        private SerializationManager serializationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueDataAdapterMigrationV1"/> class.
        /// </summary>
        /// <param name="serializationManager"></param>
        public AzureQueueDataAdapterMigrationV1(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);
            return Convert.ToBase64String(rawBytes);
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
        {
            var azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(Convert.FromBase64String(cloudMsg));
            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.serializationManager = context.GetSerializationManager();
        }
    }
}
