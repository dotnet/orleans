#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Streaming.JsonConverters
{
    internal sealed class EventSequenceTokenJsonConverter : JsonConverter<StreamSequenceToken>
    {
        private readonly Type _eventSequenceTokenType = typeof(EventSequenceToken);
        private readonly Type _eventSequenceTokenTypeV2 = typeof(EventSequenceTokenV2);

        public override bool CanConvert(Type typeToConvert) => _eventSequenceTokenType.Equals(typeToConvert)
                                                               || _eventSequenceTokenTypeV2.Equals(typeToConvert);
        public override StreamSequenceToken? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            long? sequenceNumber = null;
            int? eventIndex = null;
            
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
                        case "EventIndex":
                            eventIndex = reader.GetInt32();
                            break;
                        case "SequenceNumber":
                            sequenceNumber = reader.GetInt64();
                            break;
                    }
                }
            }

            return sequenceNumber is null 
                || eventIndex is null
                ? null
                : CreateToken(typeToConvert, sequenceNumber.Value, eventIndex.Value);
        }

        private StreamSequenceToken CreateToken(Type typeToConvert, long sequenceNumber, int eventIndex)
        {
            if (typeToConvert == _eventSequenceTokenType)
                return new EventSequenceToken(sequenceNumber, eventIndex);
            if (typeToConvert == _eventSequenceTokenTypeV2)
                return new EventSequenceTokenV2(sequenceNumber, eventIndex);
            throw new NotSupportedException($"Unsupported {nameof(StreamSequenceToken)} type: {typeToConvert}");
        }

        public override void Write(Utf8JsonWriter writer, StreamSequenceToken value, JsonSerializerOptions options) {
            writer.WriteStartObject();
            if (value is not null)
            { 
                writer.WriteString("$type", value.GetType().AssemblyQualifiedName); // For backward compatibility with Newtonsoft
                writer.WriteNumber("SequenceNumber", value.SequenceNumber);
                writer.WriteNumber("EventIndex", value.EventIndex);
            }
            writer.WriteEndObject();
        }
    }
}