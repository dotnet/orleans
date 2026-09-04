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
        private const int PartitionedStreamSequenceTokenDiscriminator = 3;
        private readonly Type _eventSequenceTokenType = typeof(EventSequenceToken);
        private readonly Type _eventSequenceTokenTypeV2 = typeof(EventSequenceTokenV2);
        private readonly Type _streamSequenceTokenType = typeof(StreamSequenceToken);
        private readonly Type _partitionedStreamSequenceTokenType = typeof(PartitionedStreamSequenceToken);

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => typeToConvert == _streamSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenTypeV2
                                                               || typeToConvert == _partitionedStreamSequenceTokenType;

        /// <inheritdoc />
        public override StreamSequenceToken? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray
                || !reader.Read()
                || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var tokenType = reader.GetInt32();
            if (tokenType == PartitionedStreamSequenceTokenDiscriminator)
            {
                return ReadPartitionedToken(ref reader, typeToConvert);
            }

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
                if (reader.TokenType != JsonTokenType.Number)
                {
                    throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
                }

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

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, StreamSequenceToken value, JsonSerializerOptions options)
        {
            var runtimeType = value.GetType();
            if (value is PartitionedStreamSequenceToken partitionedToken)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(PartitionedStreamSequenceTokenDiscriminator);
                WriteNullableString(writer, partitionedToken.ProviderIdentity);
                WriteNullableString(writer, partitionedToken.PartitionIdentity);
                writer.WriteStringValue(partitionedToken.Position);
                writer.WriteNumberValue(partitionedToken.SequenceNumber);
                writer.WriteNumberValue(partitionedToken.EventIndex);
                writer.WriteEndArray();
                return;
            }

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

        private StreamSequenceToken ReadPartitionedToken(
            ref Utf8JsonReader reader,
            Type typeToConvert)
        {
            if (typeToConvert != _streamSequenceTokenType
                && typeToConvert != _partitionedStreamSequenceTokenType)
            {
                throw new JsonException(
                    $"Cannot deserialize {nameof(PartitionedStreamSequenceToken)} as {typeToConvert}.");
            }

            var providerIdentity = ReadNullableString(ref reader);
            var partitionIdentity = ReadNullableString(ref reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var position = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var sequenceNumber = reader.GetInt64();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            var eventIndex = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            return new PartitionedStreamSequenceToken(
                providerIdentity,
                partitionIdentity,
                position,
                sequenceNumber,
                eventIndex);
        }

        private static string? ReadNullableString(ref Utf8JsonReader reader)
        {
            if (!reader.Read()
                || reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            {
                throw new JsonException($"Could not deserialize {nameof(StreamSequenceToken)}.");
            }

            return reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        }

        private static void WriteNullableString(Utf8JsonWriter writer, string? value)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }
}
