using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
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
        var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);

        switch (SerializationMode)
        {
            case SerializationMode.JsonWithFallback:
            {
                try
                {
                    return SerializeJson(azureQueueBatchMessage, streamGuid, streamNamespace);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to serialize AzureQueueBatchContainerV2 to JSON, falling back to binary serialization");
                    goto default;
                }
            }

            case SerializationMode.Json:
            {
                return SerializeJson(azureQueueBatchMessage, streamGuid, streamNamespace);
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

    private string SerializeJson(AzureQueueBatchContainerV2 batch, Guid streamGuid, string streamNamespace)
    {
        var result = JsonNode.Parse(orleansMigrationJsonSerializer.Serialize(batch, typeof(AzureQueueBatchContainerV2)))?.AsObject()
            ?? throw new JsonSerializationException("The serialized Azure Queue batch is not a JSON object.");
        result.Remove(nameof(AzureQueueBatchContainerV2.StreamGuid));
        result.Remove(nameof(AzureQueueBatchContainerV2.StreamNamespace));
        result.Add("StreamId", CreateStreamId(streamGuid, streamNamespace, GetNextReferenceId(result)));
        return result.ToJsonString(JsonNodeSerializerOptions);
    }

    private AzureQueueBatchContainerV2 DeserializeJson(string message)
    {
        var result = JsonNode.Parse(message)?.AsObject()
            ?? throw new JsonSerializationException("The Azure Queue message is not a JSON object.");
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

    private static JsonObject CreateStreamId(Guid streamGuid, string streamNamespace, int referenceId)
    {
        var namespaceBytes = streamNamespace is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(streamNamespace);
        if (namespaceBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The stream namespace is too long.", nameof(streamNamespace));
        }

        var keyBytes = Encoding.UTF8.GetBytes(streamGuid.ToString("N", CultureInfo.InvariantCulture));
        var fullKey = new byte[namespaceBytes.Length + keyBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, fullKey, 0, namespaceBytes.Length);
        Buffer.BlockCopy(keyBytes, 0, fullKey, namespaceBytes.Length, keyBytes.Length);

        return new JsonObject
        {
            ["$id"] = referenceId.ToString(CultureInfo.InvariantCulture),
            ["$type"] = "Orleans.Runtime.StreamId, Orleans.Streaming",
            ["fk"] = new JsonObject
            {
                ["$type"] = "System.Byte[], System.Private.CoreLib",
                ["$value"] = Convert.ToBase64String(fullKey)
            },
            ["ki"] = namespaceBytes.Length,
            ["fh"] = unchecked((int)ComputeStableHash(fullKey))
        };
    }

    private static int GetNextReferenceId(JsonNode value)
    {
        var maximumId = 0;
        Visit(value);
        return checked(maximumId + 1);

        void Visit(JsonNode token)
        {
            if (token is JsonObject obj)
            {
                var id = obj["$id"]?.GetValue<string>();
                if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId))
                {
                    maximumId = Math.Max(maximumId, parsedId);
                }

                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        Visit(property.Value);
                    }
                }
            }
            else if (token is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        Visit(item);
                    }
                }
            }
        }
    }

    // StableHash reads the big-endian XxHash32 digest into a native uint.
    private static uint ComputeStableHash(byte[] data)
    {
        const uint prime1 = 0x9E3779B1;
        const uint prime2 = 0x85EBCA77;
        const uint prime3 = 0xC2B2AE3D;
        const uint prime4 = 0x27D4EB2F;
        const uint prime5 = 0x165667B1;

        unchecked
        {
            var offset = 0;
            uint hash;
            if (data.Length >= 16)
            {
                var accumulator1 = prime1 + prime2;
                var accumulator2 = prime2;
                uint accumulator3 = 0;
                var accumulator4 = 0u - prime1;
                var limit = data.Length - 16;
                do
                {
                    accumulator1 = Round(accumulator1, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
                    offset += 4;
                    accumulator2 = Round(accumulator2, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
                    offset += 4;
                    accumulator3 = Round(accumulator3, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
                    offset += 4;
                    accumulator4 = Round(accumulator4, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
                    offset += 4;
                }
                while (offset <= limit);

                hash = RotateLeft(accumulator1, 1)
                    + RotateLeft(accumulator2, 7)
                    + RotateLeft(accumulator3, 12)
                    + RotateLeft(accumulator4, 18);
            }
            else
            {
                hash = prime5;
            }

            hash += (uint)data.Length;
            while (offset <= data.Length - 4)
            {
                hash += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) * prime3;
                hash = RotateLeft(hash, 17) * prime4;
                offset += 4;
            }

            while (offset < data.Length)
            {
                hash += data[offset++] * prime5;
                hash = RotateLeft(hash, 11) * prime1;
            }

            hash ^= hash >> 15;
            hash *= prime2;
            hash ^= hash >> 13;
            hash *= prime3;
            hash ^= hash >> 16;
            return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(hash) : hash;

            static uint Round(uint accumulator, uint input) => RotateLeft(accumulator + input * prime2, 13) * prime1;
            static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
        }
    }

    void IOnDeserialized.OnDeserialized(ISerializerContext context)
    {
        this.serializationManager = context.GetSerializationManager();
    }
}
