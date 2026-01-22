#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.JsonConverters
{
    /// <summary>
    /// A <see cref="JsonConverter{T}"/> for <see cref="IAsyncStream"/> types.
    /// </summary>
    internal sealed class AsyncStreamConverter(IRuntimeClient runtimeClient) : JsonConverter<IAsyncStream>
    {
        private readonly Type _asyncStreamType = typeof(IAsyncStream);

        public override bool CanConvert(Type typeToConvert) => _asyncStreamType.IsAssignableFrom(typeToConvert);

        public override IAsyncStream? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            StreamId? streamId = null;
            string? providerName = null;
            bool? isRewindable = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString()!;
                    reader.Read();
                    switch (propertyName)
                    {
                        case "streamId":
                            streamId = JsonSerializer.Deserialize<StreamId>(ref reader, options);
                            break;
                        case "providerName":
                            providerName = reader.GetString();
                            break;
                        case "isRewindable":
                            isRewindable = reader.GetBoolean();
                            break;
                    }
                }
            }

            if (streamId.HasValue && isRewindable.HasValue && !string.IsNullOrWhiteSpace(providerName))
            {
                var provider = runtimeClient.ServiceProvider
                                             .GetRequiredKeyedService<IStreamProvider>(providerName)
                                             as IInternalStreamProvider;

                return (IAsyncStream)Activator.CreateInstance(
                    typeof(StreamImpl<>).MakeGenericType(typeToConvert.GetGenericArguments()),
                    new QualifiedStreamId(providerName, streamId.Value),
                    provider,
                    isRewindable.Value,
                    runtimeClient)!;
            }
            else
            {
                return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, IAsyncStream value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("streamId");
            JsonSerializer.Serialize(writer, value.StreamId, options);
            writer.WritePropertyName("providerName");
            writer.WriteStringValue(value.ProviderName);
            writer.WritePropertyName("isRewindable");
            writer.WriteBooleanValue(value.IsRewindable);
            writer.WriteEndObject();
        }
    }
}
