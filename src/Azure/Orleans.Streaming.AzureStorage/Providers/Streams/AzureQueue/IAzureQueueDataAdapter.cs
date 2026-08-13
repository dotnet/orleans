using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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
        public AzureQueueDataAdapterV1(Serializer serializer)
        {
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
        public AzureQueueDataAdapterV2(Serializer serializer)
        {
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
            if (!Guid.TryParseExact(streamId.GetKeyAsString(), "N", out var streamKey))
            {
                throw new InvalidDataException("The compact JSON envelope only supports GUID-keyed streams.");
            }

            var serializedEvents = _jsonSerializer.Serialize(events.Cast<object>().ToList(), typeof(List<object>));
            var serializedRequestContext = _jsonSerializer.Serialize(
                requestContext ?? new Dictionary<string, object>(),
                typeof(Dictionary<string, object>));
            using var eventsDocument = JsonDocument.Parse(serializedEvents);
            using var requestContextDocument = JsonDocument.Parse(serializedRequestContext);
            using var textWriter = new StringWriter(CultureInfo.InvariantCulture);
            using var jsonWriter = new JsonTextWriter(textWriter) { Formatting = Formatting.None };
            jsonWriter.WriteStartObject();
            jsonWriter.WritePropertyName("version");
            jsonWriter.WriteValue(CompactEnvelopeVersion);
            jsonWriter.WritePropertyName("stream");
            jsonWriter.WriteStartObject();
            jsonWriter.WritePropertyName("namespace");
            jsonWriter.WriteValue(streamNamespace);
            jsonWriter.WritePropertyName("key");
            jsonWriter.WriteValue(streamKey.ToString("D", CultureInfo.InvariantCulture));
            jsonWriter.WriteEndObject();
            jsonWriter.WritePropertyName("events");
            WriteCollectionValues(jsonWriter, eventsDocument.RootElement);
            jsonWriter.WritePropertyName("requestContext");
            jsonWriter.WriteStartObject();
            foreach (var property in requestContextDocument.RootElement.EnumerateObject())
            {
                if (property.Name is not "$id" and not "$type")
                {
                    jsonWriter.WritePropertyName(property.Name);
                    jsonWriter.WriteRawValue(property.Value.GetRawText());
                }
            }

            jsonWriter.WriteEndObject();
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return textWriter.ToString();
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
            var streamKeyText = keyElement.GetString();
            if (!Guid.TryParseExact(streamKeyText, "D", out var streamKey))
            {
                throw new InvalidDataException("The Azure Queue JSON envelope stream key is not a GUID in D format.");
            }

            var events = _jsonSerializer.Deserialize(typeof(List<object>), eventsElement.GetRawText()) as List<object>
                ?? throw new InvalidDataException("The Azure Queue JSON envelope events could not be deserialized.");
            var requestContext = _jsonSerializer.Deserialize(
                typeof(Dictionary<string, object>),
                requestContextElement.GetRawText()) as Dictionary<string, object>
                ?? throw new InvalidDataException("The Azure Queue JSON envelope request context could not be deserialized.");

            batch = new AzureQueueBatchContainerV2(StreamId.Create(streamNamespace, streamKey), events, requestContext);
            return true;
        }

        private static void WriteCollectionValues(JsonTextWriter writer, JsonElement serializedCollection)
        {
            var values = serializedCollection.ValueKind switch
            {
                JsonValueKind.Array => serializedCollection,
                JsonValueKind.Object when serializedCollection.TryGetProperty("$values", out var result)
                    && result.ValueKind == JsonValueKind.Array => result,
                _ => throw new InvalidDataException("The serialized event collection is not a JSON array.")
            };

            writer.WriteStartArray();
            foreach (var item in values.EnumerateArray())
            {
                writer.WriteRawValue(item.GetRawText());
            }

            writer.WriteEndArray();
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
