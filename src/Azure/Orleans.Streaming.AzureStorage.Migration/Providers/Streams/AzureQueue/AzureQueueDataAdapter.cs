using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.Migration.Configuration;
using Orleans.Streams;
using Orleans.Persistence.Migration.Serialization;

namespace Orleans.Providers.Streams.AzureQueue.Migration;

/// <summary>
/// Data adapter that uses types that support custom serializers (like json).
/// </summary>
public class AzureQueueDataAdapterMigrationV1 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
{
    private SerializationManager serializationManager;
    private readonly AzureQueueMigrationOptions options;
    private readonly OrleansMigrationJsonSerializer orleansMigrationJsonSerializer;

    private SerializationMode SerializationMode => options.SerializationMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureQueueDataAdapterMigrationV1"/> class.
    /// </summary>
    /// <param name="serializationManager"></param>
    /// <param name="orleansMigrationJsonSerializer"></param>
    /// <param name="options"></param>
    public AzureQueueDataAdapterMigrationV1(
        SerializationManager serializationManager,
        OrleansMigrationJsonSerializer orleansMigrationJsonSerializer,
        AzureQueueMigrationOptions options)
    {
        this.serializationManager = serializationManager;
        this.orleansMigrationJsonSerializer = orleansMigrationJsonSerializer;
        this.options = options;
    }

    /// <summary>
    /// Creates a cloud queue message from stream event data.
    /// </summary>
    public string ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);

        switch (SerializationMode)
        {
            case SerializationMode.PrioritizeJson:
                try
                {
                    return orleansMigrationJsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2));
                }
                catch
                {
                    // log?
                    goto default;
                }

            case SerializationMode.Json:
                return orleansMigrationJsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2));

            default:
                var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);
                return Convert.ToBase64String(rawBytes);
        }
    }

    /// <summary>
    /// Creates a batch container from a cloud queue message
    /// </summary>
    public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
    {
        AzureQueueBatchContainerV2 azureQueueBatch;
        switch (SerializationMode)
        {
            case SerializationMode.PrioritizeJson:
                try
                {
                    azureQueueBatch = (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                }
                catch
                {
                    // log?
                    goto default;
                }
                break;

            case SerializationMode.Json:
                azureQueueBatch = (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                break;

            default:
                azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(Convert.FromBase64String(cloudMsg));
                break;
        }

        azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
        return azureQueueBatch;
    }

    void IOnDeserialized.OnDeserialized(ISerializerContext context)
    {
        this.serializationManager = context.GetSerializationManager();
    }
}
