#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Streaming.JsonConverters
{
    /// <summary>
    /// A <see cref="JsonConverter{T}"/> for <see cref="EventSequenceToken"/> and <see cref="EventSequenceTokenV2"/> types.
    /// </summary>
    public sealed class EventSequenceTokenJsonConverter : JsonConverter<StreamSequenceToken>
    {
        private const int EventSequenceTokenDiscriminator = 1;
        private const int EventSequenceTokenV2Discriminator = 2;
        private readonly Type _eventSequenceTokenType = typeof(EventSequenceToken);
        private readonly Type _eventSequenceTokenTypeV2 = typeof(EventSequenceTokenV2);
        private readonly Type _streamSequenceTokenType = typeof(StreamSequenceToken);

        public override bool CanConvert(Type typeToConvert) => typeToConvert == _streamSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenTypeV2;

        public override StreamSequenceToken? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray
                || !reader.Read()
                || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var tokenType = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var sequenceNumber = reader.GetInt64();
            var eventIndex = 0;
            if (!reader.Read())
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                eventIndex = reader.GetInt32();
                if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                {
                    throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
                }
            }

            return CreateToken(typeToConvert, tokenType, sequenceNumber, eventIndex);
        }

        private StreamSequenceToken CreateToken(Type typeToConvert, int tokenType, long sequenceNumber, int eventIndex)
        {
            var runtimeType = tokenType switch
            {
                EventSequenceTokenDiscriminator => _eventSequenceTokenType,
                EventSequenceTokenV2Discriminator => _eventSequenceTokenTypeV2,
                _ => throw new JsonException($"Unsupported {nameof(StreamSequenceToken)} type: {tokenType}"),
            };

            if (typeToConvert != _streamSequenceTokenType && typeToConvert != runtimeType)
            {
                throw new JsonException($"Cannot deserialize {runtimeType} as {typeToConvert}.");
            }

            return runtimeType == _eventSequenceTokenType
                ? new EventSequenceToken(sequenceNumber, eventIndex)
                : new EventSequenceTokenV2(sequenceNumber, eventIndex);
        }

        public override void Write(Utf8JsonWriter writer, StreamSequenceToken value, JsonSerializerOptions options)
        {
            var runtimeType = value.GetType();
            if (runtimeType != _eventSequenceTokenType && runtimeType != _eventSequenceTokenTypeV2)
            {
                throw new NotSupportedException($"Unsupported {nameof(StreamSequenceToken)} type: {runtimeType}");
            }

            writer.WriteStartArray();
            writer.WriteNumberValue(runtimeType == _eventSequenceTokenType ? EventSequenceTokenDiscriminator : EventSequenceTokenV2Discriminator);
            writer.WriteNumberValue(value.SequenceNumber);
            if (value.EventIndex != 0)
            {
                writer.WriteNumberValue(value.EventIndex);
            }

            writer.WriteEndArray();
        }
    }
}