#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Streams;

namespace Orleans.Streaming.JsonConverters
{
    internal class StreamImplConverter : JsonConverter
    {
        private readonly IRuntimeClient _runtimeClient;

        public StreamImplConverter(IRuntimeClient runtimeClient)
        {
            _runtimeClient = runtimeClient;
        }

        public override bool CanConvert(Type objectType)
            => objectType.IsGenericType && (objectType.GetGenericTypeDefinition() == typeof(StreamImpl<>) || objectType.GetGenericTypeDefinition() == typeof(IAsyncStream<>));

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            var jo = JObject.Load(reader);
            if (jo != null)
            {
                var itemType = objectType.GetGenericArguments()[0];
                var fullType = typeof(StreamImpl<>).MakeGenericType(itemType);

                var streamId = jo["streamId"]?.ToObject<StreamId>();
                var providerName = jo["providerName"]?.Value<string>();
                var isRewindable = jo["isRewindable"]?.ToObject<bool>();

                if (streamId.HasValue && isRewindable.HasValue && !string.IsNullOrWhiteSpace(providerName))
                {
                    var provider = _runtimeClient.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(providerName) as IInternalStreamProvider;
                    return Activator.CreateInstance(fullType, new QualifiedStreamId(providerName, streamId.Value), provider, isRewindable.Value, _runtimeClient);
                }
            }
            throw new SerializationException();
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            if (value is IAsyncStream target)
            {
                writer.WritePropertyName("streamId");
                serializer.Serialize(writer, target.StreamId);
                writer.WritePropertyName("providerName");
                serializer.Serialize(writer, target.ProviderName);
                writer.WritePropertyName("isRewindable");
                writer.WriteValue(target.IsRewindable);
            }
            writer.WriteEndObject();
        }
    }
}
