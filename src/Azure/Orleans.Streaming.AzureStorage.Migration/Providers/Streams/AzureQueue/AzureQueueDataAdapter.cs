using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.Migration.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue.Migration;

/// <summary>
/// Data adapter that uses types that support custom serializers (like json).
/// </summary>
public class AzureQueueDataAdapterMigrationV1 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
{
    private SerializationManager serializationManager;
    private readonly OrleansMigrationJsonSerializer orleansMigrationJsonSerializer;

    private readonly AzureQueueMigrationOptions options;
    private readonly ILogger logger;

    private SerializationMode SerializationMode => options.SerializationMode;
    private DeserializationMode DeserializationMode => options.DeserializationMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureQueueDataAdapterMigrationV1"/> class.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="serializationManager"></param>
    /// <param name="orleansMigrationJsonSerializer"></param>
    /// <param name="options"></param>
    public AzureQueueDataAdapterMigrationV1(
        ILogger<AzureQueueDataAdapterMigrationV1> logger,
        SerializationManager serializationManager,
        OrleansMigrationJsonSerializer orleansMigrationJsonSerializer,
        AzureQueueMigrationOptions options)
    {
        this.serializationManager = serializationManager;
        this.orleansMigrationJsonSerializer = orleansMigrationJsonSerializer;

        this.logger = logger;
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
            case SerializationMode.JsonWithFallback:
            {
                try
                {
                    return orleansMigrationJsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2));
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to serialize AzureQueueBatchContainerV2 to JSON, falling back to binary serialization");
                    goto default;
                }
            }

            case SerializationMode.Json:
            {
                return orleansMigrationJsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2));
            }

            case SerializationMode.Binary:
            default:
            {
                var rawBytes = this.serializationManager.SerializeToByteArray(azureQueueBatchMessage);
                return Convert.ToBase64String(rawBytes);
            }
        }
    }

    /// <summary>
    /// Creates a batch container from a cloud queue message
    /// </summary>
    public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
    {
        AzureQueueBatchContainerV2 azureQueueBatch;
        switch (DeserializationMode)
        {
            case DeserializationMode.PreferJson:
                try
                {
                    azureQueueBatch = (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to Deserialize AzureQueueBatchContainerV2 from JSON");
                    azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(Convert.FromBase64String(cloudMsg));
                }
                break;


            case DeserializationMode.PreferBinary:
            default:
                try
                {
                    azureQueueBatch = this.serializationManager.DeserializeFromByteArray<AzureQueueBatchContainerV2>(Convert.FromBase64String(cloudMsg));
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to Deserialize AzureQueueBatchContainerV2 via binary format");
                    azureQueueBatch = (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                }
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
