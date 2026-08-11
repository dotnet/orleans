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
        private readonly Type _eventSequenceTokenType = typeof(EventSequenceToken);
        private readonly Type _eventSequenceTokenTypeV2 = typeof(EventSequenceTokenV2);
        private readonly Type _streamSequenceTokenType = typeof(StreamSequenceToken);

        public override bool CanConvert(Type typeToConvert) => typeToConvert == _streamSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenType
                                                               || typeToConvert == _eventSequenceTokenTypeV2;

        public override StreamSequenceToken? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            long? sequenceNumber = null;
            int? eventIndex = null;
            string? serializedType = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "$ref":
                            throw new JsonException("Reference-preserving JSON is not supported by the System.Text.Json grain storage serializer.");
                        case "EventIndex":
                            eventIndex = reader.GetInt32();
                            break;
                        case "SequenceNumber":
                            sequenceNumber = reader.GetInt64();
                            break;
                        case "$type":
                            serializedType = reader.GetString();
                            break;
                    }
                }
            }

            return sequenceNumber is null
                || eventIndex is null
                ? null
                : CreateToken(typeToConvert, serializedType, sequenceNumber.Value, eventIndex.Value);
        }

        private StreamSequenceToken CreateToken(Type typeToConvert, string? serializedType, long sequenceNumber, int eventIndex)
        {
            if (typeToConvert == _eventSequenceTokenType)
                return new EventSequenceToken(sequenceNumber, eventIndex);
            if (typeToConvert == _eventSequenceTokenTypeV2)
                return new EventSequenceTokenV2(sequenceNumber, eventIndex);

            if (IsSerializedType(serializedType, _eventSequenceTokenType))
                return new EventSequenceToken(sequenceNumber, eventIndex);
            if (IsSerializedType(serializedType, _eventSequenceTokenTypeV2))
                return new EventSequenceTokenV2(sequenceNumber, eventIndex);

            throw new NotSupportedException($"Unsupported {nameof(StreamSequenceToken)} type: {typeToConvert}");
        }

        private static bool IsSerializedType(string? serializedType, Type expectedType)
            => serializedType is not null
               && serializedType.StartsWith($"{expectedType.FullName},", StringComparison.Ordinal);

        public override void Write(Utf8JsonWriter writer, StreamSequenceToken value, JsonSerializerOptions options)
        {
            var runtimeType = value.GetType();
            if (runtimeType != _eventSequenceTokenType && runtimeType != _eventSequenceTokenTypeV2)
            {
                throw new NotSupportedException($"Unsupported {nameof(StreamSequenceToken)} type: {runtimeType}");
            }

            writer.WriteStartObject();
            writer.WriteString("$type", runtimeType.AssemblyQualifiedName); // For backward compatibility with Newtonsoft
            writer.WriteNumber("SequenceNumber", value.SequenceNumber);
            writer.WriteNumber("EventIndex", value.EventIndex);
            writer.WriteEndObject();
        }
    }
}