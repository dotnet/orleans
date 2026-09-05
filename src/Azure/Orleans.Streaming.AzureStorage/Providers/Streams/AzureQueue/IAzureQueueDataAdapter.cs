using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue.Json;
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
        /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
        public AzureQueueDataAdapterV1(Serializer serializer)
        {
            ArgumentNullException.ThrowIfNull(serializer);
            this.serializer = serializer.GetSerializer<AzureQueueBatchContainer>();
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
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
            // A valid queue message contains a serialized batch container.
            var azureQueueBatch = this.serializer.Deserialize(Convert.FromBase64String(cloudMsg))!;
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
        /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
        public AzureQueueDataAdapterV2(Serializer serializer)
        {
            ArgumentNullException.ThrowIfNull(serializer);
            this.serializer = serializer.GetSerializer<AzureQueueBatchContainerV2>();
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
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
            // A valid queue message contains a serialized batch container.
            var azureQueueBatch = this.serializer.Deserialize(Convert.FromBase64String(cloudMsg))!;
            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            this.serializer = context.ServiceProvider.GetRequiredService<Serializer<AzureQueueBatchContainerV2>>();
        }
    }

    /// <summary>
    /// Data adapter that uses OrleansJsonSerializer for serializing stream event data with fallback support.
    /// This adapter is experimental and subject to change in future updates.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    public class AzureQueueJsonDataAdapter : IQueueDataAdapter<string, IBatchContainer>
    {
        private const int CompactEnvelopeVersion = 1;
        private readonly AzureQueueJsonDataAdapterOptions _options;
        private readonly ILogger<AzureQueueJsonDataAdapter> _logger;
        private readonly OrleansJsonSerializer _jsonSerializer;
        private readonly IQueueDataAdapter<string, IBatchContainer> _fallbackAdapter;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueJsonDataAdapter"/> class.
        /// </summary>
        /// <param name="jsonSerializer">The JSON serializer.</param>
        /// <param name="fallbackAdapter">The fallback data adapter (typically AzureQueueDataAdapterV2).</param>
        /// <param name="options">The adapter options.</param>
        /// <param name="logger">The logger.</param>
        public AzureQueueJsonDataAdapter(
            OrleansJsonSerializer jsonSerializer,
            AzureQueueDataAdapterV2 fallbackAdapter,
            AzureQueueJsonDataAdapterOptions options,
            ILogger<AzureQueueJsonDataAdapter> logger)
        {
            _jsonSerializer = jsonSerializer;
            _fallbackAdapter = fallbackAdapter;
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
        {
            var eventList = events.ToList();

            try
            {
                return _options.PreferJson
                    ? SerializeJson(streamId, eventList, requestContext)
                    : _fallbackAdapter.ToQueueMessage(streamId, eventList, token, requestContext);
            }
            catch (Exception ex) when (_options.EnableFallback)
            {
                if (_options.PreferJson)
                {
                    _logger.LogDebug(ex, "JSON serialization failed for stream {StreamId}, falling back to binary serialization", streamId);
                    return _fallbackAdapter.ToQueueMessage(streamId, eventList, token, requestContext);
                }

                _logger.LogDebug(ex, "Binary serialization failed for stream {StreamId}, falling back to JSON serialization", streamId);
                return SerializeJson(streamId, eventList, requestContext);
            }
        }

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        public IBatchContainer FromQueueMessage(string cloudMsg, long sequenceId)
        {
            ArgumentException.ThrowIfNullOrEmpty(cloudMsg, nameof(cloudMsg));

            try
            {
                if (_options.PreferJson)
                {
                    return DeserializeJson(cloudMsg, sequenceId);
                }

                return _fallbackAdapter.FromQueueMessage(cloudMsg, sequenceId);
            }
            catch (Exception ex) when (_options.EnableFallback)
            {
                if (_options.PreferJson)
                {
                    _logger.LogDebug(ex, "Failed to deserialize cloud message using JSON, falling back to binary deserialization");
                    return _fallbackAdapter.FromQueueMessage(cloudMsg, sequenceId);
                }

                _logger.LogDebug(ex, "Binary deserialization failed, falling back to JSON deserialization");
                return DeserializeJson(cloudMsg, sequenceId);
            }
        }

        internal static AzureQueueJsonDataAdapter Create(IServiceProvider services, string name)
        {
            var jsonSerializer = new OrleansJsonSerializer(Options.Create(services.GetOptionsByName<OrleansJsonSerializerOptions>(name)));
            var fallbackAdapter = ActivatorUtilities.CreateInstance<AzureQueueDataAdapterV2>(services);
            var options = services.GetOptionsByName<AzureQueueJsonDataAdapterOptions>(name);
            var logger = services.GetRequiredService<ILogger<AzureQueueJsonDataAdapter>>();

            return new AzureQueueJsonDataAdapter(jsonSerializer, fallbackAdapter, options, logger);
        }

        private AzureQueueBatchContainerV2 DeserializeJson(string cloudMsg, long sequenceId)
        {
            if (TryDeserializeCompactJson(cloudMsg, out var compactBatch))
            {
                compactBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
                return compactBatch;
            }

            if (_jsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg) is not AzureQueueBatchContainerV2 azureQueueBatch)
            {
                throw new InvalidDataException("The queue message did not contain an Azure Queue batch.");
            }

            azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        private string SerializeJson<T>(StreamId streamId, List<T> events, Dictionary<string, object>? requestContext)
        {
            var streamNamespace = streamId.GetNamespace();
            var streamKey = streamId.GetKeyAsString();

            var serializedEvents = _jsonSerializer.Serialize(events.Cast<object>().ToList(), typeof(List<object>));
            var serializedRequestContext = _jsonSerializer.Serialize(
                requestContext ?? new Dictionary<string, object>(),
                typeof(Dictionary<string, object>));
            var serializedEventNode = JsonNode.Parse(serializedEvents)
                ?? throw new InvalidDataException("The serialized event collection is not valid JSON.");
            var eventValues = serializedEventNode switch
            {
                JsonArray array => array,
                JsonObject obj when obj["$values"] is JsonArray array => array,
                _ => throw new InvalidDataException("The serialized event collection is not a JSON array.")
            };
            RemoveUnreferencedIds(eventValues);

            var requestContextObject = JsonNode.Parse(serializedRequestContext) as JsonObject
                ?? throw new InvalidDataException("The serialized request context is not a JSON object.");
            requestContextObject.Remove("$type");
            RemoveUnreferencedIds(requestContextObject);

            var bufferWriter = new ArrayBufferWriter<byte>();
            using var jsonWriter = new Utf8JsonWriter(
                bufferWriter,
                new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", CompactEnvelopeVersion);
            jsonWriter.WriteStartObject("stream");
            if (streamNamespace is null)
            {
                jsonWriter.WriteNull("namespace");
            }
            else
            {
                jsonWriter.WriteString("namespace", streamNamespace);
            }

            jsonWriter.WriteString("key", streamKey);
            jsonWriter.WriteEndObject();
            jsonWriter.WritePropertyName("events");
            eventValues.WriteTo(jsonWriter);
            jsonWriter.WritePropertyName("requestContext");
            requestContextObject.WriteTo(jsonWriter);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        }

        private bool TryDeserializeCompactJson(string cloudMsg, out AzureQueueBatchContainerV2 batch)
        {
            using var document = JsonDocument.Parse(cloudMsg);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionElement))
            {
                batch = null!;
                return false;
            }

            if (versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != CompactEnvelopeVersion)
            {
                throw new InvalidDataException($"Unsupported Azure Queue JSON envelope version: {versionElement.GetRawText()}.");
            }

            var streamElement = GetRequiredProperty(root, "stream", JsonValueKind.Object);
            if (!streamElement.TryGetProperty("namespace", out var namespaceElement)
                || namespaceElement.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
            {
                throw new InvalidDataException("The Azure Queue JSON envelope property 'namespace' must be a String or Null.");
            }

            var keyElement = GetRequiredProperty(streamElement, "key", JsonValueKind.String);
            var eventsElement = GetRequiredProperty(root, "events", JsonValueKind.Array);
            var requestContextElement = GetRequiredProperty(root, "requestContext", JsonValueKind.Object);
            var streamNamespace = namespaceElement.ValueKind == JsonValueKind.Null ? null : namespaceElement.GetString();
            var streamKey = keyElement.GetString()
                ?? throw new InvalidDataException("The Azure Queue JSON envelope stream key is missing.");

            var events = _jsonSerializer.Deserialize(typeof(List<object>), eventsElement.GetRawText()) as List<object>
                ?? throw new InvalidDataException("The Azure Queue JSON envelope events could not be deserialized.");
            var requestContext = _jsonSerializer.Deserialize(
                typeof(Dictionary<string, object>),
                requestContextElement.GetRawText()) as Dictionary<string, object>
                ?? throw new InvalidDataException("The Azure Queue JSON envelope request context could not be deserialized.");

            batch = new AzureQueueBatchContainerV2(StreamId.Create(streamNamespace, streamKey), events, requestContext);
            return true;
        }

        private static void RemoveUnreferencedIds(JsonNode value)
        {
            var referencedIds = new HashSet<string>(StringComparer.Ordinal);
            CollectReferences(value);
            RemoveIds(value);

            void CollectReferences(JsonNode node)
            {
                if (node is JsonObject obj)
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
                else if (node is JsonArray array)
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

            void RemoveIds(JsonNode node)
            {
                if (node is JsonObject obj)
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
                else if (node is JsonArray array)
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

        private static JsonElement GetRequiredProperty(JsonElement parent, string name, JsonValueKind valueKind)
        {
            if (!parent.TryGetProperty(name, out var result) || result.ValueKind != valueKind)
            {
                throw new InvalidDataException($"The Azure Queue JSON envelope property '{name}' must be a {valueKind}.");
            }

            return result;
        }
    }
}
