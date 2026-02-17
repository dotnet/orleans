using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Data adapter that uses OrleansJsonSerializer for serializing stream event data with fallback support.
    /// This adapter is experimental and subject to change in future updates.
    /// </summary>
    [Experimental("StreamingJsonSerializationExperimental", UrlFormat = "https://github.com/dotnet/orleans/pull/9618")]
    [SerializationCallbacks(typeof(OnDeserializedCallbacks))]
    public class AzureQueueJsonDataAdapter : IQueueDataAdapter<string, IBatchContainer>, IOnDeserialized
    {
        private readonly AzureQueueJsonDataAdapterOptions _options;
        private readonly ILogger<AzureQueueJsonDataAdapter> _logger;

        private OrleansJsonSerializer _jsonSerializer;
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
        public string ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamId, events.Cast<object>().ToList(), requestContext);

            try
            {
                return _options.PreferJson
                    ? _jsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2))
                    : _fallbackAdapter.ToQueueMessage(streamId, events, token, requestContext);
            }
            catch (Exception ex) when (this._options.EnableFallback)
            {
                if (_options.PreferJson)
                {
                    _logger.LogDebug(ex, "JSON serialization failed for stream {StreamId}, falling back to binary serialization", streamId);
                    return _fallbackAdapter.ToQueueMessage(streamId, events, token, requestContext);
                }
                else
                {
                    _logger.LogDebug(ex, "Binary serialization failed for stream {StreamId}, falling back to JSON serialization", streamId);
                    return _jsonSerializer.Serialize(azureQueueBatchMessage, typeof(AzureQueueBatchContainerV2));
                }
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
                    var azureQueueBatch = (AzureQueueBatchContainerV2)_jsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                    azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
                    return azureQueueBatch;
                }
                else
                {
                    return _fallbackAdapter.FromQueueMessage(cloudMsg, sequenceId);
                }
            }
            catch (Exception ex) when (_options.EnableFallback)
            {
                if (_options.PreferJson)
                {
                    _logger.LogDebug(ex, "Failed to deserialize cloud message using JSON, falling back to binary deserialization");
                    return _fallbackAdapter.FromQueueMessage(cloudMsg, sequenceId);
                }
                else
                {
                    _logger.LogDebug(ex, "Binary deserialization failed for cloudMsg {cloudMsg}, falling back to JSON deserialization", cloudMsg);
                    var azureQueueBatch = (AzureQueueBatchContainerV2)_jsonSerializer.Deserialize(typeof(AzureQueueBatchContainerV2), cloudMsg);
                    azureQueueBatch.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
                    return azureQueueBatch;
                }
            }
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            _jsonSerializer = context.ServiceProvider.GetRequiredService<OrleansJsonSerializer>();
        }
    }
}
