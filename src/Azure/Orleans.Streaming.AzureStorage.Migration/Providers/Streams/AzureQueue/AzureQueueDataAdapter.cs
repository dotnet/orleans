using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.Migration.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue.Migration;

/// <summary>
/// Converts Azure Queue stream messages between the Orleans 3.x binary format and a migration-compatible JSON format.
/// </summary>
public class AzureQueueDataAdapterMigrationV1 : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
{
    private static readonly JsonSerializerOptions JsonNodeSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private SerializationManager serializationManager;
    private readonly OrleansMigrationJsonSerializer orleansMigrationJsonSerializer;

    private readonly AzureQueueMigrationOptions options;
    private readonly ILogger logger;

    private SerializationMode SerializationMode => options.SerializationMode;
    private DeserializationMode DeserializationMode => options.DeserializationMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureQueueDataAdapterMigrationV1"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="serializationManager">The Orleans binary serialization manager.</param>
    /// <param name="orleansMigrationJsonSerializer">The JSON serializer used for migration payloads.</param>
    /// <param name="options">The Azure Queue migration options.</param>
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
    /// <typeparam name="T">The stream event type.</typeparam>
    /// <param name="streamGuid">The stream identifier.</param>
    /// <param name="streamNamespace">The stream namespace.</param>
    /// <param name="events">The events to include in the message.</param>
    /// <param name="token">The stream sequence token.</param>
    /// <param name="requestContext">The request context to propagate with the events.</param>
    /// <returns>The serialized queue message.</returns>
    public string ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var eventList = events.Cast<object>().ToList();
        var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, eventList, requestContext);

        switch (SerializationMode)
        {
            case SerializationMode.JsonWithFallback:
            {
                try
                {
                    return SerializeJson(streamGuid, streamNamespace, eventList, requestContext);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to serialize AzureQueueBatchContainerV2 to JSON, falling back to binary serialization");
                    goto default;
                }
            }

            case SerializationMode.Json:
            {
                return SerializeJson(streamGuid, streamNamespace, eventList, requestContext);
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
    /// Creates a batch container from a cloud queue message.
    /// </summary>
    /// <param name="cloudMsg">The serialized queue message.</param>
    /// <param name="sequenceId">The queue sequence identifier.</param>
    /// <returns>The deserialized batch container.</returns>
    public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
    {
        AzureQueueBatchContainerV2 azureQueueBatch;
        switch (DeserializationMode)
        {
            case DeserializationMode.PreferJson:
                try
                {
                    azureQueueBatch = DeserializeJson(cloudMsg);
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
                    azureQueueBatch = DeserializeJson(cloudMsg);
                }
                break;
        }

        azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
        return azureQueueBatch;
    }

    private string SerializeJson(
        Guid streamGuid,
        string streamNamespace,
        List<object> events,
        Dictionary<string, object> requestContext)
    {
        var serializedEvents = JsonNode.Parse(
            orleansMigrationJsonSerializer.Serialize(events, typeof(List<object>)))?.AsObject()
            ?? throw new JsonSerializationException("The serialized event collection is not a JSON object.");
        var eventValues = serializedEvents["$values"]?.AsArray()
            ?? throw new JsonSerializationException("The serialized event collection does not contain values.");
        serializedEvents.Remove("$values");
        RemoveUnreferencedIds(eventValues);

        var serializedRequestContext = JsonNode.Parse(
            orleansMigrationJsonSerializer.Serialize(requestContext, typeof(Dictionary<string, object>)))
            ?? new JsonObject();
        if (serializedRequestContext is JsonObject requestContextObject)
        {
            requestContextObject.Remove("$type");
            RemoveUnreferencedIds(requestContextObject);
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["stream"] = new JsonObject
            {
                ["namespace"] = streamNamespace,
                ["key"] = streamGuid.ToString("D")
            },
            ["events"] = eventValues,
            ["requestContext"] = serializedRequestContext
        }.ToJsonString(JsonNodeSerializerOptions);
    }

    private AzureQueueBatchContainerV2 DeserializeJson(string message)
    {
        var result = JsonNode.Parse(message)?.AsObject()
            ?? throw new JsonSerializationException("The Azure Queue message is not a JSON object.");
        if (result["version"] is JsonValue)
        {
            return DeserializeMigrationEnvelope(result);
        }

        if (result["StreamId"] is not JsonObject streamId)
        {
            return (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(
                typeof(AzureQueueBatchContainerV2),
                message);
        }

        var fullKey = Convert.FromBase64String(streamId["fk"]?["$value"]?.GetValue<string>()
            ?? throw new JsonSerializationException("The StreamId full key is missing."));
        var keyIndex = streamId["ki"]?.GetValue<int>()
            ?? throw new JsonSerializationException("The StreamId key index is missing.");

        if ((uint)keyIndex > (uint)fullKey.Length)
        {
            throw new JsonSerializationException("The StreamId key index is invalid.");
        }

        var streamNamespace = keyIndex == 0 ? null : Encoding.UTF8.GetString(fullKey, 0, keyIndex);
        var streamKey = Encoding.UTF8.GetString(fullKey, keyIndex, fullKey.Length - keyIndex);
        if (!Guid.TryParseExact(streamKey, "N", out var streamGuid))
        {
            throw new JsonSerializationException("The StreamId key is not a legacy GUID stream key.");
        }

        result.Remove("StreamId");
        result.Add(nameof(AzureQueueBatchContainerV2.StreamGuid), streamGuid);
        result.Add(nameof(AzureQueueBatchContainerV2.StreamNamespace), streamNamespace);

        return (AzureQueueBatchContainerV2)orleansMigrationJsonSerializer.Deserialize(
            typeof(AzureQueueBatchContainerV2),
            result.ToJsonString(JsonNodeSerializerOptions));
    }

    private AzureQueueBatchContainerV2 DeserializeMigrationEnvelope(JsonObject envelope)
    {
        var version = envelope["version"]?.GetValue<int>()
            ?? throw new JsonSerializationException("The migration envelope version is missing.");
        if (version != 1)
        {
            throw new JsonSerializationException($"Unsupported Azure Queue migration envelope version '{version}'.");
        }

        var stream = envelope["stream"]?.AsObject()
            ?? throw new JsonSerializationException("The migration envelope stream is missing.");
        var streamNamespace = stream["namespace"]?.GetValue<string>();
        var streamKey = stream["key"]?.GetValue<string>()
            ?? throw new JsonSerializationException("The migration envelope stream key is missing.");
        if (!Guid.TryParseExact(streamKey, "D", out var streamGuid))
        {
            throw new JsonSerializationException("The migration envelope stream key is not a GUID.");
        }

        var serializedEvents = envelope["events"]?.ToJsonString(JsonNodeSerializerOptions)
            ?? throw new JsonSerializationException("The migration envelope events are missing.");
        var events = (List<object>)orleansMigrationJsonSerializer.Deserialize(typeof(List<object>), serializedEvents);

        var requestContext = envelope["requestContext"] is { } requestContextNode
            ? (Dictionary<string, object>)orleansMigrationJsonSerializer.Deserialize(
                typeof(Dictionary<string, object>),
                requestContextNode.ToJsonString(JsonNodeSerializerOptions))
            : null;

        return new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events, requestContext);
    }

    private static void RemoveUnreferencedIds(JsonNode value)
    {
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        CollectReferences(value);
        RemoveIds(value);

        void CollectReferences(JsonNode token)
        {
            if (token is JsonObject obj)
            {
                if (obj["$ref"]?.GetValue<string>() is { } reference)
                {
                    referencedIds.Add(reference);
                }

                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        CollectReferences(property.Value);
                    }
                }
            }
            else if (token is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        CollectReferences(item);
                    }
                }
            }
        }

        void RemoveIds(JsonNode token)
        {
            if (token is JsonObject obj)
            {
                if (obj["$id"]?.GetValue<string>() is { } id && !referencedIds.Contains(id))
                {
                    obj.Remove("$id");
                }

                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        RemoveIds(property.Value);
                    }
                }
            }
            else if (token is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        RemoveIds(item);
                    }
                }
            }
        }
    }

    void IOnDeserialized.OnDeserialized(ISerializerContext context)
    {
        this.serializationManager = context.GetSerializationManager();
    }
}
