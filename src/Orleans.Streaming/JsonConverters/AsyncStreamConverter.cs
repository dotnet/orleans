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
            if (reader.TokenType != JsonTokenType.StartArray
                || !reader.Read()
                || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Could not deserialize {nameof(IAsyncStream)}.");
            }

            var providerName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException($"Could not deserialize {nameof(IAsyncStream)}.");
            }

            var streamId = JsonSerializer.Deserialize<StreamId>(ref reader, options);
            if (!reader.Read()
                || reader.TokenType != JsonTokenType.EndArray
                || streamId == default
                || string.IsNullOrWhiteSpace(providerName))
            {
                throw new JsonException($"Could not deserialize {nameof(IAsyncStream)}.");
            }

            if (typeToConvert.GetGenericArguments() is not [var itemType])
            {
                throw new JsonException($"Cannot deserialize a stream reference as non-generic type {typeToConvert}.");
            }

            var streamProvider = runtimeClient.ServiceProvider.GetRequiredKeyedService<IStreamProvider>(providerName);
            if (streamProvider is not IInternalStreamProvider provider)
            {
                throw new JsonException($"Stream provider '{providerName}' does not support internal stream references.");
            }

            return (IAsyncStream)Activator.CreateInstance(
                typeof(StreamImpl<>).MakeGenericType(itemType),
                new QualifiedStreamId(providerName, streamId),
                provider,
                streamProvider.IsRewindable,
                runtimeClient)!;
        }

        public override void Write(Utf8JsonWriter writer, IAsyncStream value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value.ProviderName);
            JsonSerializer.Serialize(writer, value.StreamId, options);
            writer.WriteEndArray();
        }
    }
}
